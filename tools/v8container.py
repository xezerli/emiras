import struct,zlib,sys
def block(d,off):
    h=d[off:off+31]; 
    doc,page,nxt=int(h[2:10],16),int(h[11:19],16),int(h[20:28],16)
    return doc,page,nxt,off+31
def read_doc(d,off):
    out=b'';first=True;total=None
    while True:
        doc,page,nxt,o=block(d,off)
        if first: total=doc;first=False
        out+=d[o:o+page]
        if nxt==0x7fffffff: break
        off=nxt
    return out[:total]
def entries(d):
    toc=read_doc(d,16)
    res=[]
    for i in range(0,len(toc),12):
        a,b,c=struct.unpack('<3I',toc[i:i+12])
        if a==0x7fffffff: break
        hdr=read_doc(d,a)
        name=hdr[20:].decode('utf-16le',errors='replace').rstrip('\x00')
        res.append((name,a,b))
    return res
def data(d,off):
    raw=read_doc(d,off)
    try: return zlib.decompress(raw,-15)
    except Exception: return raw
if __name__=='__main__':
    d=open(sys.argv[1],'rb').read()
    for n,a,c in entries(d):
        x=data(d,c); print(n,len(x),x[:60])
