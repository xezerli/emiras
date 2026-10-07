-- Sxem davranış testləri: search_path=t_demo,public ilə işlədin. Hər test gözlənilən xətanı yoxlayır.
\set ON_ERROR_STOP on
BEGIN;
INSERT INTO branches(code,name) VALUES ('B1','Mərkəz');
INSERT INTO users(email_hash,email_enc,password_hash,full_name,specialty)
 VALUES ('\x01','\x01','x','Dr A','general'),('\x02','\x02','x','Dr B','general');
INSERT INTO patients(branch_id,first_name,last_name) SELECT id,'Əli','Əliyev' FROM branches;
INSERT INTO appointments(branch_id,patient_id,provider_id,period)
 SELECT b.id,p.id,u.id,tstzrange(now(), now()+interval '30 min')
 FROM branches b, patients p, users u WHERE u.full_name='Dr A';

-- 1) eyni həkim üzrə üst-üstə düşən qəbul rədd olunmalıdır
SAVEPOINT s1;
DO $$ BEGIN
  INSERT INTO appointments(branch_id,patient_id,provider_id,period)
   SELECT b.id,p.id,u.id,tstzrange(now()+interval '10 min', now()+interval '40 min')
   FROM branches b, patients p, users u WHERE u.full_name='Dr A';
  RAISE EXCEPTION 'TEST FAIL: double booking allowed';
EXCEPTION WHEN exclusion_violation THEN RAISE NOTICE 'OK 1: double booking blocked'; END $$;

-- 2) başqa həkim eyni vaxtda olar
INSERT INTO appointments(branch_id,patient_id,provider_id,period)
 SELECT b.id,p.id,u.id,tstzrange(now(), now()+interval '30 min')
 FROM branches b, patients p, users u WHERE u.full_name='Dr B';

-- 3) payments dəyişməzdir
INSERT INTO payments(patient_id,method,amount) SELECT id,'cash',10 FROM patients;
DO $$ BEGIN
  UPDATE payments SET amount=1;
  RAISE EXCEPTION 'TEST FAIL: payment updated';
EXCEPTION WHEN raise_exception THEN
  IF SQLERRM LIKE 'TEST FAIL%' THEN RAISE; END IF; RAISE NOTICE 'OK 3: payments immutable'; END $$;

-- 4) audit append-only
INSERT INTO audit_log(action,hash) VALUES ('test','\xab');
DO $$ BEGIN
  DELETE FROM audit_log;
  RAISE EXCEPTION 'TEST FAIL: audit deleted';
EXCEPTION WHEN raise_exception THEN
  IF SQLERRM LIKE 'TEST FAIL%' THEN RAISE; END IF; RAISE NOTICE 'OK 4: audit append-only'; END $$;

-- 5) row_version artır, change_log yazılır
UPDATE patients SET last_name='Əliyev2';
DO $$ DECLARE v int; c int; BEGIN
  SELECT row_version INTO v FROM patients;
  SELECT count(*) INTO c FROM change_log WHERE entity='patients';
  IF v <> 2 OR c <> 2 THEN RAISE EXCEPTION 'TEST FAIL: version=% changelog=%', v, c; END IF;
  RAISE NOTICE 'OK 5: row_version + change_log';
END $$;

-- 6) imzalanmış qeyd dəyişməzdir
INSERT INTO clinical_notes(patient_id,author_id,subjective,signed_at)
 SELECT p.id,u.id,'x',now() FROM patients p, users u WHERE u.full_name='Dr A';
DO $$ BEGIN
  UPDATE clinical_notes SET subjective='y';
  RAISE EXCEPTION 'TEST FAIL: signed note changed';
EXCEPTION WHEN raise_exception THEN
  IF SQLERRM LIKE 'TEST FAIL%' THEN RAISE; END IF; RAISE NOTICE 'OK 6: signed note immutable'; END $$;
ROLLBACK;
