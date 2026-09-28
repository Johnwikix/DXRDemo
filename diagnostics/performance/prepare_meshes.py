"""Normalize PLY meshes and build a 12-bin SAH software BVH, leaf size <= 8."""
from pathlib import Path
import json, struct, sys, time
import numpy as np

root=Path(__file__).resolve().parent/'models'
names=sys.argv[1:] or ['bun_zipper','Armadillo','dragon_vrip_res4','dragon_vrip_res3','dragon_vrip_res2','dragon_vrip']

def read_ply(path):
    with path.open('rb') as f:
        header=[]
        while True:
            line=f.readline().decode('ascii').strip(); header.append(line)
            if line=='end_header': break
        nv=int(next(x.split()[-1] for x in header if x.startswith('element vertex')))
        nf=int(next(x.split()[-1] for x in header if x.startswith('element face')))
        if 'format ascii 1.0' in header:
            vertices=np.loadtxt(f,max_rows=nv,usecols=(0,1,2),dtype=np.float32)
            faces=np.loadtxt(f,max_rows=nf,dtype=np.int32)
            assert (faces[:,0]==3).all()
            faces=faces[:,1:4]
        else:
            assert path.stem=='Armadillo' and 'format binary_big_endian 1.0' in header
            vertices=np.fromfile(f,dtype='>f4',count=nv*3).reshape(nv,3).astype(np.float32)
            packed=np.fromfile(f,dtype=np.dtype([('intensity','u1'),('count','u1'),('ids','>i4',(3,))]),count=nf)
            assert (packed['count']==3).all()
            faces=packed['ids'].astype(np.int32)
    assert (faces>=0).all() and (faces<nv).all()
    lo,hi=vertices.min(axis=0),vertices.max(axis=0)
    vertices -= np.array([(lo[0]+hi[0])/2,lo[1],(lo[2]+hi[2])/2],np.float32)
    vertices *= 2/max(hi-lo)
    return vertices[faces]

def build(name):
    started=time.perf_counter()
    tri=read_ply(root/(name+'.ply'))
    lower,upper=tri.min(axis=1),tri.max(axis=1)
    centers=(lower+upper)*0.5
    nodes=[]; order=[]; max_depth=0
    def area(lo,hi):
        d=np.maximum(0,hi-lo)
        return d[...,0]*d[...,1]+d[...,1]*d[...,2]+d[...,2]*d[...,0]
    def recurse(ids,depth,parent=-1):
        nonlocal max_depth
        max_depth=max(depth,max_depth)
        idx=len(nodes); nodes.append(None)
        lo,hi=lower[ids].min(axis=0),upper[ids].max(axis=0)
        if len(ids)>8 and depth<60:
            c=centers[ids]; cmin,cmax=c.min(axis=0),c.max(axis=0)
            best_cost=np.inf; best_axis=-1; best_bin=0; best_bins=None
            for axis in range(3):
                if cmax[axis]-cmin[axis] < 1e-10: continue
                bins=np.minimum(11,((c[:,axis]-cmin[axis])*(12/(cmax[axis]-cmin[axis]))).astype(np.int32))
                counts=np.bincount(bins,minlength=12)
                blo=np.full((12,3),np.inf); bhi=np.full((12,3),-np.inf)
                np.minimum.at(blo,bins,lower[ids]); np.maximum.at(bhi,bins,upper[ids])
                lc=np.cumsum(counts)[:-1]; rc=np.cumsum(counts[::-1])[::-1][1:]
                ll=np.minimum.accumulate(blo,axis=0)[:-1]; lh=np.maximum.accumulate(bhi,axis=0)[:-1]
                rl=np.minimum.accumulate(blo[::-1],axis=0)[::-1][1:]; rh=np.maximum.accumulate(bhi[::-1],axis=0)[::-1][1:]
                costs=lc*area(ll,lh)+rc*area(rl,rh)
                costs[(lc==0)|(rc==0)]=np.inf
                split=int(np.argmin(costs))
                if costs[split] < best_cost:
                    best_cost=float(costs[split]); best_axis=axis; best_bin=split; best_bins=bins
            if best_axis>=0:
                mask=best_bins<=best_bin
                recurse(ids[mask],depth+1,idx); right=len(nodes); recurse(ids[~mask],depth+1,idx)
                nodes[idx]=(lo,hi,-1,0,len(nodes),right,parent,best_axis)
                return
        first=len(order); order.extend(ids.tolist())
        nodes[idx]=(lo,hi,first,len(ids),len(nodes),-1,parent,0)
    recurse(np.arange(len(tri)),0)
    # DFS-preorder escape links: skipping a node skips its entire subtree.
    packed=np.zeros((len(nodes),3,4),np.float32)
    for i,(lo,hi,start,count,escape,right,parent,axis) in enumerate(nodes):
        packed[i,0,:3]=lo-1e-6; packed[i,0,3]=start
        packed[i,1,:3]=hi+1e-6; packed[i,1,3]=count
        packed[i,2,0]=escape if escape<len(nodes) else -1
        packed[i,2,1]=right
        packed[i,2,2]=parent
        packed[i,2,3]=axis
    tri=tri[np.asarray(order)]
    # Reconstruct hardware triangle vertices from the same stored edges used by SM.
    data=np.zeros((len(tri),3,4),np.float32)
    data[:,0,:3]=tri[:,0]
    data[:,1,:3]=tri[:,1]-tri[:,0]
    data[:,2,:3]=tri[:,2]-tri[:,0]
    with (root/(name+'.mesh')).open('wb') as f:
        f.write(struct.pack('<II',len(data),len(packed)))
        f.write(data.astype('<f4').tobytes()); f.write(packed.astype('<f4').tobytes())
    metadata={'name':name,'triangles':len(tri),'nodes':len(nodes),'max_depth':max_depth,'leaf_limit':8,'build_seconds':time.perf_counter()-started}
    (root/(name+'.json')).write_text(json.dumps(metadata,indent=2))
    print(json.dumps(metadata),flush=True)
for name in names: build(name)
