-- T004: Scheduling context
CREATE TABLE provider_schedules (          -- həkimin həftəlik iş qrafiki
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider_id uuid NOT NULL REFERENCES users(id),
    branch_id uuid NOT NULL REFERENCES branches(id),
    weekday smallint NOT NULL CHECK (weekday BETWEEN 1 AND 7),
    start_time time NOT NULL, end_time time NOT NULL, CHECK (end_time > start_time),
    valid_from date NOT NULL DEFAULT current_date, valid_to date
);
CREATE INDEX ix_provider_sched ON provider_schedules(provider_id, weekday);

CREATE TABLE time_off (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider_id uuid NOT NULL REFERENCES users(id),
    period tstzrange NOT NULL, reason text
);
CREATE INDEX ix_time_off ON time_off USING gist (provider_id, period);

CREATE TABLE appointment_recurrences (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    rrule text NOT NULL,                    -- RFC 5545
    until date, created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE appointments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id uuid NOT NULL REFERENCES branches(id),
    patient_id uuid NOT NULL REFERENCES patients(id),
    provider_id uuid NOT NULL REFERENCES users(id),
    room_id uuid REFERENCES rooms(id),
    period tstzrange NOT NULL CHECK (NOT isempty(period) AND lower_inf(period) = false AND upper_inf(period) = false),
    status text NOT NULL DEFAULT 'booked' CHECK (status IN
        ('booked','confirmed','checked_in','in_progress','completed','cancelled','no_show','rescheduled')),
    reason text, procedure_hint text,
    source text NOT NULL DEFAULT 'reception' CHECK (source IN ('reception','online','phone','ai','app')),
    recurrence_id uuid REFERENCES appointment_recurrences(id),
    external_event_id text,                 -- Google/Outlook sync
    cancel_reason text, cancelled_at timestamptz, checked_in_at timestamptz,
    created_by uuid REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
    row_version int NOT NULL DEFAULT 1,
    -- Double-booking DB səviyyəsində bloklanır (race condition-a qarşı son müdafiə)
    CONSTRAINT ex_provider_no_overlap EXCLUDE USING gist (provider_id WITH =, period WITH &&)
        WHERE (status IN ('booked','confirmed','checked_in','in_progress')),
    CONSTRAINT ex_room_no_overlap EXCLUDE USING gist (room_id WITH =, period WITH &&)
        WHERE (room_id IS NOT NULL AND status IN ('booked','confirmed','checked_in','in_progress'))
);
CREATE INDEX ix_appt_calendar ON appointments USING gist (branch_id, period);
CREATE INDEX ix_appt_patient ON appointments(patient_id, (lower(period)) DESC);
CREATE INDEX ix_appt_provider_day ON appointments(provider_id, (lower(period)));
CREATE INDEX ix_appt_noshow ON appointments(patient_id) WHERE status = 'no_show';

CREATE TABLE appointment_reminders (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    appointment_id uuid NOT NULL REFERENCES appointments(id) ON DELETE CASCADE,
    channel text NOT NULL CHECK (channel IN ('sms','whatsapp','telegram','email','push')),
    send_at timestamptz NOT NULL, sent_at timestamptz, status text NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending','sent','failed','cancelled')), error text
);
CREATE INDEX ix_reminders_due ON appointment_reminders(send_at) WHERE status = 'pending';

CREATE TABLE waitlist_entries (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL REFERENCES patients(id), branch_id uuid NOT NULL REFERENCES branches(id),
    provider_id uuid REFERENCES users(id), earliest timestamptz, latest timestamptz,
    priority smallint NOT NULL DEFAULT 5, status text NOT NULL DEFAULT 'waiting'
        CHECK (status IN ('waiting','offered','booked','expired')),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_waitlist ON waitlist_entries(branch_id, priority, created_at) WHERE status = 'waiting';

CREATE TABLE queue_tickets (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id uuid NOT NULL REFERENCES branches(id),
    appointment_id uuid REFERENCES appointments(id), patient_id uuid REFERENCES patients(id),
    ticket_no int NOT NULL, queue_date date NOT NULL DEFAULT current_date,
    status text NOT NULL DEFAULT 'waiting' CHECK (status IN ('waiting','called','serving','done','left')),
    created_at timestamptz NOT NULL DEFAULT now(), called_at timestamptz, finished_at timestamptz,
    UNIQUE (branch_id, queue_date, ticket_no)
);

SELECT attach_standard_triggers('appointments');
