import zlib, struct, sys
def blk(d,addr):
    h=d[addr:addr+31]
    tot=int(h[2:10],16); page=int(h[11:19],16); nxt=int(h[20:28],16)
    out=d[addr+31:addr+31+page]
    while nxt!=0x7fffffff and len(out)<tot:
        a=nxt; h=d[a:a+31]; page=int(h[11:19],16); nxt=int(h[20:28],16)
        out+=d[a+31:a+31+page]
    return out[:tot]
def parse(d):
    """-> dict(head=16 bytes, entries=[dict(hdr,name,comp,data|sub)])"""
    toc=blk(d,16); ents=[]
    for i in range(0,len(toc),12):
        a,b,_=struct.unpack('<III',toc[i:i+12])
        hd=blk(d,a); name=hd[20:].decode('utf-16le').rstrip('\x00'); body=blk(d,b)
        try: raw=zlib.decompress(body,-15); comp=True
        except Exception: raw=body; comp=False
        e=dict(hdr=hd,name=name,comp=comp,orig=body)
        if raw[:4]==b'\xff\xff\xff\x7f':
            try: e['sub']=parse(raw); 
            except Exception: e['data']=raw
        else: e['data']=raw
        ents.append(e)
    return dict(head=d[:16],entries=ents)
def doc(body,page):
    page=max(page,len(body)) if page else len(body)
    return b'\r\n%08x %08x %08x \r\n'%(len(body),page,0x7fffffff)+body+b'\0'*(page-len(body))
def build(c,level=9):
    ents=c['entries']; n=len(ents)
    toclen=12*n; tocdoc=doc(b'\0'*toclen,max(0x200,toclen))
    pos=16+len(tocdoc); toc=b''; blocks=b''
    for e in ents:
        if 'sub' in e: raw=build(e['sub'],level)
        else: raw=e['data']
        if e['comp']:
            co=zlib.compressobj(level,zlib.DEFLATED,-15); body=co.compress(raw)+co.flush()
        else: body=raw
        h=doc(e['hdr'],len(e['hdr'])); b=doc(body,max(0x200,len(body)))
        toc+=struct.pack('<III',pos,pos+len(h),0x7fffffff)
        blocks+=h+b; pos+=len(h)+len(b)
    tocdoc=doc(toc,0x200 if toclen<=0x200 else toclen)
    return c['head']+tocdoc+blocks
def tree(c,p=''):
    for e in c['entries']:
        if 'sub' in e: yield from tree(e['sub'],p+e['name']+'/')
        else: yield p+e['name'],e['data']
