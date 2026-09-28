"""Validate geometry/path outputs and produce scientific benchmark artifacts."""
from pathlib import Path
import json
import numpy as np
from PIL import Image, ImageDraw

root=Path(__file__).resolve().parent/'mesh-output'
rows=[]
for path in sorted(root.glob('*-1280x720-4spp-results.json')):
    info=json.loads(path.read_text())
    prefix=str(path)[:-len('-results.json')]
    h,w=info['height'],info['width']
    def read(backend):
        a=np.fromfile(prefix+'-'+backend+'.f32',np.float32).reshape(h,w,4)
        assert np.isfinite(a).all(),backend+' produced NaN/Inf'
        return a
    a,b=read('dxr-primary'),read('sm-primary')
    both=(a[:,:,3]>0)&(b[:,:,3]>0)
    mismatch=np.mean((a[:,:,3]>0)!=(b[:,:,3]>0))
    depth=np.abs(a[both,3]-b[both,3])
    dxr,sm,actual=read('dxr-a'),read('sm-bytecode'),read('computesharp')
    compute_error=float(np.max(np.abs(sm-actual)))
    mae=float(np.mean(np.abs(dxr[:,:,:3]-sm[:,:,:3])))
    assert mismatch < 0.001, 'Primary coverage mismatch'
    assert float(np.mean(depth)) < 0.0001, 'Primary depth mismatch'
    assert compute_error < 1e-6, 'Not the same ComputeSharp output'
    assert mae < 0.01, 'Path output mismatch'
    hardware=(info['results'][0]['gpu']['median_ms']+info['results'][2]['gpu']['median_ms'])/2
    software=info['results'][1]['gpu']['median_ms']
    row={'model':info['model'],'triangles':info['triangleCount'],'dxr_gpu_ms':hardware,'sm_gpu_ms':software,'speedup':software/hardware,
        'computesharp_wall_ms':info['results'][3]['wall']['median_ms'],
        'primary_mesh_coverage_percent':100*float(np.mean(a[:,:,3]>0)),
        'primary_hit_mask_mismatch_percent':100*float(mismatch),'primary_depth_mae':float(np.mean(depth)),
        'computesharp_max_difference':compute_error,'path_linear_rgb_mae':mae}
    rows.append(row)
    for backend,data in [('dxr',dxr),('sm',sm)]:
        rgb=np.power(np.clip(data[:,:,:3]/(1+data[:,:,:3]),0,1),1/2.2)
        Image.fromarray((rgb*255).astype('uint8')).save(prefix+'-'+backend+'.png')
    print(json.dumps(row),flush=True)
rows.sort(key=lambda r:r['triangles'])
(root/'summary.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
(root.parent/'mesh-summary.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')

selected=['bun_zipper','Armadillo','dragon_vrip']
canvas=Image.new('RGB',(1280,3*400),(20,23,28));draw=ImageDraw.Draw(canvas)
for row,name in enumerate(selected):
    item=next(x for x in rows if x['model']==name)
    for col,backend in enumerate(['dxr','sm']):
        image=Image.open(root/f'{name}-1280x720-4spp-{backend}.png').resize((640,360))
        canvas.paste(image,(col*640,row*400+40))
        label=f"{name} | {item['triangles']:,} triangles | {'DXR' if backend=='dxr' else 'ComputeSharp software BVH'} | {item['dxr_gpu_ms'] if backend=='dxr' else item['sm_gpu_ms']:.3f} ms"
        draw.text((col*640+12,row*400+14),label,fill=(240,240,240))
canvas.save(root/'comparison.png')
