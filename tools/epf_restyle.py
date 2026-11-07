#!/usr/bin/env python3
"""РМК2.epf -> тёмная тема (эксперимент): правит цвета в тексте обычной формы. Исходный epf не меняется."""
import sys,zlib
sys.path.insert(0,'/home/user/emiras/tools')
import v8container as v8, v8writer as v8w, restyle
src,dst=sys.argv[1],sys.argv[2]
d=open(src,'rb').read()
parts=v8w.raw_parts(d)
def tryinf(x):
    try: return zlib.decompress(x,-15),True
    except Exception: return x,False
# найти форму «Форма» РМК2 — самая большая nested-форма
best=None
for i,(n,h,b) in enumerate(parts):
    if n.endswith('.0'):
        inner,c=tryinf(b)
        if inner[:4]==b'\xff\xff\xff\x7f':
            ip=v8w.raw_parts(inner); body=dict((p[0],p[2]) for p in ip)
            if 'form' in body and (best is None or len(body['form'])>best[0]): best=(len(body['form']),i,ip,c)
_,idx,ip,c1=best
pal=dict(text=restyle.rgb(0xE8,0xEE,0xF5),panel=restyle.rgb(0x18,0x20,0x2A),btn=restyle.rgb(0x2A,0x35,0x44))
newip=[]
for n,h,b in ip:
    if n=='form':
        txt,_=tryinf(b); s=txt.decode('utf-8-sig')
        log=[]; s2=restyle.restyle(s,pal,log); print('контролов изменено:',log[0])
        b='﻿'.encode('utf-8')+s2.encode('utf-8')
    newip.append((n,h,b))
inner=v8w.pack([p[0] for p in newip],[p[1] for p in newip],[p[2] for p in newip],len(newip))
parts[idx]=(parts[idx][0],parts[idx][1],v8w.deflate(inner) if c1 else inner)
out=v8w.pack([p[0] for p in parts],[p[1] for p in parts],[p[2] for p in parts],len(parts))
open(dst,'wb').write(out); print('OK',dst,len(out))
