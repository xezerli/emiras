import re,sys
def parse_spans(s):
    """returns tree of (start,end,children_or_value). Lists: ('L',start,end,[children]); atoms: ('A',start,end,text)"""
    i=0
    def atom():
        nonlocal i
        st=i
        if s[i]=='"':
            j=i+1
            while True:
                if s[j]=='"':
                    if s[j+1:j+2]=='"': j+=2; continue
                    break
                j+=1
            i=j+1; return ('A',st,i,s[st:i])
        m=re.compile(r'[^,}\r\n]+').match(s,i); i=m.end(); return ('A',st,i,s[st:i].strip())
    def lst():
        nonlocal i
        st=i; i+=1; ch=[]
        if s.startswith('#base64:',i):
            j=s.index('}',i); i=j+1; return ('L',st,i,[])
        while True:
            while s[i] in '\r\n\t ': i+=1
            if s[i]=='}': i+=1; return ('L',st,i,ch)
            if s[i]==',': i+=1; continue
            ch.append(lst() if s[i]=='{' else atom())
    while s[i] in '﻿ \r\n': i+=1
    return lst()
def is_color(n):
    return n[0]=='L' and len(n[3])==3 and n[3][0][0]=='A' and n[3][0][3]=='3' and n[3][2][0]=='L'
def color_txt(bgr): return '{3,0,\r\n{%d}\r\n}'%bgr
def rgb(r,g,b): return b<<16|g<<8|r
def restyle(s, pal, log=None):
    root=parse_spans(s)
    edits=[]
    def walk(n,depth=0):
        if n[0]!='L': return
        ch=n[3]
        if len(ch)>10 and ch[0][3:4]==('16',) and ch[1][3:4]==('1',) and all(is_color(ch[k]) for k in (2,3,6,7,8,9)):
            edits.append(ch)
        for c in ch: walk(c,depth+1)
    walk(root)
    reps=[]
    for ch in edits:
        def col(k): 
            c=ch[k]; return (c[3][1][3], c[3][2][3][1:-1] if False else None)
        def kv(k):
            c=ch[k]; kind=c[3][1][3]; v=c[3][2][3][0][3] if c[3][2][3] else None
            return kind,v
        k2,v2=kv(2); k3,v3=kv(3); k9,v9=kv(9)
        # текст
        reps.append((ch[2][1],ch[2][2],color_txt(pal['text'])))
        # фон: всегда тёмный
        reps.append((ch[3][1],ch[3][2],color_txt(pal['panel'])))
        # фон кнопки/поля (индекс 9): если авто/системный — тёмно-серый, явные цвета сохраняем
        if k9 in ('3','4'):
            reps.append((ch[9][1],ch[9][2],color_txt(pal['btn'])))
    reps.sort(key=lambda r:-r[0])
    out=s
    for a,b,t in reps: out=out[:a]+t+out[b:]
    if log is not None: log.append(len(edits))
    return out
