import struct,zlib
import v8container as v8
def _blk(doc,page,nxt): return b'\r\n%08x %08x %08x \r\n'%(doc,page,nxt)
def pack(entries, hdrs, bodies, third):
    """entries: names; hdrs/bodies: bytes (body already in stored form). returns container bytes"""
    n=len(entries)
    out=bytearray(struct.pack('<4I',0x7fffffff,512,third,0))
    toc_off=16
    toc_size=12*n; toc_page=max(toc_size,512)
    toc_pos=len(out)
    out+=b'\0'*(31+toc_page)
    pos=len(out); table=[]
    for h,b in zip(hdrs,bodies):
        ha=len(out); out+=_blk(len(h),len(h),0x7fffffff)+h
        ba=len(out); page=max(len(b),512)
        out+=_blk(len(b),page,0x7fffffff)+b+b'\0'*(page-len(b))
        table.append((ha,ba))
    toc=b''.join(struct.pack('<3I',a,b,0x7fffffff) for a,b in table)
    out[toc_pos:toc_pos+31+toc_page]=_blk(toc_size,toc_page,0x7fffffff)+toc+b'\0'*(toc_page-len(toc))
    return bytes(out)
def raw_parts(d):
    """returns list of (name, hdr_bytes, body_raw_bytes_as_stored)"""
    toc=v8.read_doc(d,16)
    res=[]
    for i in range(0,len(toc),12):
        a,b,c=struct.unpack('<3I',toc[i:i+12])
        if a==0x7fffffff: break
        h=v8.read_doc(d,a); name=h[20:].decode('utf-16le').rstrip('\0')
        res.append((name,h,v8.read_doc(d,b)))
    return res
def deflate(x):
    c=zlib.compressobj(9,zlib.DEFLATED,-15); return c.compress(x)+c.flush()
