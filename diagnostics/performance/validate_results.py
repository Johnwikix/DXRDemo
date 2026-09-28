"""Readback comparison and a numeric reproduction of the glass self-hit."""
import json
import math
from pathlib import Path

root = Path(__file__).resolve().parent
base = (root / 'variants/baseline.hlsl.rgba').read_bytes()
results = {}
for name in ['analytic_raygen', 'analytic.cs', 'inline_query.cs', 'offset_direction']:
    actual = (root / f'variants/{name}.hlsl.rgba').read_bytes()
    assert len(base) == len(actual)
    differences = [abs(a-b) for i, (a,b) in enumerate(zip(base,actual)) if i % 4 != 3]
    results[name] = {
        'rgb_mean_absolute_error_out_of_255': sum(differences)/len(differences),
        'rgb_channels_within_one_code_value_percent': 100*sum(d<=1 for d in differences)/len(differences),
        'rgb_rmse_out_of_255': math.sqrt(sum(d*d for d in differences)/len(differences)),
    }

def dot(a,b): return sum(x*y for x,y in zip(a,b))
def add(a,b): return tuple(x+y for x,y in zip(a,b))
def scale(a,s): return tuple(x*s for x in a)
def norm(a): return scale(a,1/math.sqrt(dot(a,a)))
def hit(ro,rd):
    a,b,c=dot(rd,rd),dot(ro,rd),dot(ro,ro)-1
    discriminant=b*b-a*c
    t=(-b-math.sqrt(discriminant))/a
    if t < 0.001: t=(-b+math.sqrt(discriminant))/a
    return t

ro=(0,0,-3)
rd=norm((0.25,0,1))
p=add(ro,scale(rd,hit(ro,rd)))
n=norm(p)
eta=1/1.5
cosine=dot(rd,n)
refracted=norm(add(scale(rd,eta),scale(n,-(eta*cosine+math.sqrt(1-eta*eta*(1-cosine*cosine))))))
results['glass_self_hit']={
    'incoming_origin':ro, 'incoming_direction':rd, 'hit_position':p,
    'transmitted_direction_dot_normal':dot(refracted,n), 'ray_t_min':0.001,
    'next_hit_t_original_positive_normal_offset':hit(add(p,scale(n,0.001)),refracted),
    'next_hit_t_directional_offset':hit(add(p,scale(n,-0.001)),refracted),
}
text=json.dumps(results,indent=2)
(root/'validation.json').write_text(text,encoding='utf-8')
print(text)
