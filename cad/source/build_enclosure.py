"""Desk R6 first-fit enclosure: deterministic, millimetre solid CSG.
Python 3.11+; install the adjacent requirements.txt in a virtual environment.
Run from any working directory. Outputs go to cad/generated unless overridden.
No firmware, serial, Bluetooth, or hardware operations.
"""
from pathlib import Path
import argparse, json, math, hashlib, csv
import numpy as np
import trimesh
import manifold3d as md

HERE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output-dir', type=Path, default=HERE.parent/'generated',
                    help='Generated artifacts directory (default: cad/generated)')
args = parser.parse_args()
OUT = args.output_dir.resolve()
for sub in ['stl','3mf','source','preview','qa']:
    (OUT/sub).mkdir(parents=True, exist_ok=True)
P = dict(width=160.0, depth=166.0, body_height=60.0, floor=2.8, wall=3.0,
         lid=2.4, corner_radius=8.0, screw_clearance=2.9, nut_slot_width=5.4,
         tft_center=[-30.0,50.0], encoder_center=[48.0,48.0],
         perma_center=[27.0,-24.0], battery_center=[-47.0,28.0],
         battery_bay=[38.0,68.0], perma_post=10.0, encoder_standoff=8.0,
         button_standoff=8.0, tft_spacer=3.0, button_hole=14.0, encoder_hole=22.0,
         tft_window=[54.0,40.0], tft_recess=[62.0,44.0,1.0],
         service_center_x=-13.236, service_width=36.0, service_bottom=14.0,
         left_service_y=47.0, left_service_width=34.0, left_service_bottom=38.0)
param_file = HERE/'parameters.json'
if param_file.exists(): P.update(json.loads(param_file.read_text(encoding='utf8')))
W,D,H,F,T = [P[k] for k in ['width','depth','body_height','floor','lid']]
EPS=0.03

def box(w,d,h,x=0,y=0,z=0):
    return md.Manifold.cube([w,d,h]).translate([x-w/2,y-d/2,z])
def cyl(d,h,x=0,y=0,z=0,n=64):
    return md.Manifold.cylinder(h,d/2,circular_segments=n).translate([x,y,z])
def add(xs): return md.Manifold.batch_boolean(xs,md.OpType.Add)
def cut(a,xs): return md.Manifold.batch_boolean([a]+xs,md.OpType.Subtract)
def rr(w,d,h,r,x=0,y=0,z=0):
    return add([box(w-2*r,d,h,x,y,z),box(w,d-2*r,h,x,y,z)]+[
        cyl(2*r,h,x+sx*(w/2-r),y+sy*(d/2-r),z) for sx in [-1,1] for sy in [-1,1]])
def hexprism(af,h,x,y,z):
    return md.Manifold.cylinder(h,af/math.sqrt(3),circular_segments=6).translate([x,y,z])
def side_nut_pocket(x,y,z,width=None,height=3.0,direction=1,reach=8):
    # Cylinder vertices at 0 degrees => horizontal X tips, across Y flats.
    width = P['nut_slot_width'] if width is None else width
    return add([hexprism(width,height,x,y,z),
        box(reach,width,height,x+direction*reach/2,y,z)])
def holes_at(points,z=-1,h=100,d=None):
    return [cyl(d or P['screw_clearance'],h,x,y,z) for x,y in points]

corners=[(sx*(W/2-9),sy*(D/2-9)) for sy in [-1,1] for sx in [-1,1]]
tx,ty=P['tft_center']; ex,ey=P['encoder_center']; px,py=P['perma_center']; bx,by=P['battery_center']
tft_holes=[(tx+sx*29.845,ty+sy*23.749) for sy in [-1,1] for sx in [-1,1]]
encoder_holes=[(ex+sx*10.16,ey+sy*10.16) for sy in [-1,1] for sx in [-1,1]]
perma_holes=[(px,py+sy*36.83) for sy in [-1,1]]
buttons=[]
for idx,(x,y) in enumerate([(-38,0),(20,0),(-38,-27),(20,-27),(-38,-54),(20,-54)],1):
    buttons.append(dict(id=f'K{idx}',cap=[x,y],pcb_center=[x+4.8926,y],holes=[(x+16.3926,y-7.4),(x+16.3926,y+7.4)]))
button_holes=[pt for b in buttons for pt in b['holes']]

shell=cut(rr(W,D,H,P['corner_radius']),[rr(W-2*P['wall'],D-2*P['wall'],H,P['corner_radius']-P['wall'],z=F)])
extras=[]; cutters=[]
for x,y in corners:
    extras.append(box(14,14,H,x,y))
    cutters += [side_nut_pocket(x,y,H-6, direction=-1 if x>0 else 1,height=3.2,reach=9),cyl(P['screw_clearance'],11,x,y,H-10)]
for x,y in perma_holes:
    extras.append(box(10,10,P['perma_post']+EPS,x,y,F-EPS))
    cutters += [side_nut_pocket(x,y,F+P['perma_post']-5,height=3,direction=-1,reach=6),cyl(P['screw_clearance'],P['perma_post'],x,y,F+.3)]
# Battery low fence: smooth compartment, no screws under the pouch; rear exit gap.
bw,bd=P['battery_bay']
tray=cut(rr(bw+4,bd+4,3,3,bx,by,F-EPS),[rr(bw,bd,4,1,bx,by,F-.1),box(16,6,5,bx,by+bd/2,F-.1)])
extras.append(tray)
# Strap slots reside OUTSIDE the battery fence. A soft strap is an additional item.
for x in [bx-bw/2-5,bx+bw/2+5]:
    extras.append(box(5,12,6,x,by,F-EPS))
    cutters.append(box(7,9,2,x,by,F+2))
# Three wire tie saddles, not in battery area. Additional ties required.
for x,y in [(63,-35),(63,9),(-8,-68)]:
    extras.append(box(8,12,6,x,y,F-EPS)); cutters.append(box(10,8,2,x,y,F+2))
cutters.append(box(P['service_width'],10,H-P['service_bottom']+2,P['service_center_x'],D/2,P['service_bottom']))
cutters.append(box(10,P['left_service_width'],H-P['left_service_bottom']+2,-W/2,P['left_service_y'],P['left_service_bottom']))
base=cut(add([shell]+extras),cutters)

# Lid is authored in installed orientation and exported outer face down on bed.
lid=rr(W,D,T,P['corner_radius'],z=H)
lid_cuts=holes_at(corners+tft_holes+encoder_holes+button_holes,H-5,12)
lid_cuts += [rr(*P['tft_window'],T+2,1,tx+1.99,ty+.05,H-1),
             rr(P['tft_recess'][0],P['tft_recess'][1],P['tft_recess'][2]+EPS,.5,tx-1.135,ty,H-EPS),
             cyl(P['encoder_hole'],T+2,ex,ey,H-1)]
lid_cuts += [cyl(P['button_hole'],T+2,*b['cap'],H-1) for b in buttons]
lid=cut(lid,lid_cuts)
lid=add([lid,box(122,2.4,3+EPS,0,20,H-3),box(2.4,85,3+EPS,0,-24,H-3),box(116,2.4,3+EPS,0,-76,H-3)])
lid_print=lid.rotate([180,0,0]).translate([0,0,H+T])
spacer=cut(cyl(4.4,P['tft_spacer']),[cyl(P['screw_clearance'],P['tft_spacer']+2,z=-1)])

# A 1:1 TFT panel sample reproduces the real lid window, recess, hole positions.
tft_coupon = (lid ^ box(71.6,59.6,T,tx,ty,H)).translate([-tx,-ty,-H]).rotate([180,0,0]).translate([0,0,T])
# Button/encoder exact panel coupon with the same real mounting holes.
input_coupon=rr(80,54,T,3)
icuts=[cyl(22,T+2,-22,0,-1)]+holes_at([(-22+sx*10.16,sy*10.16) for sx in [-1,1] for sy in [-1,1]],-1,T+2)
icuts += [cyl(14,T+2,15,0,-1)] + holes_at([(31.3926,-7.4),(31.3926,7.4)],-1,T+2)
input_coupon=cut(input_coupon,icuts)
# Small tolerance coupon. Left-to-right order documented, no size scaling.
tolerance=rr(100,52,2.4,3)
tolcuts=[]
for x,diam in zip([-36,-18,0,18,36],[2.7,2.9,3.1,3.3,3.5]): tolcuts.append(cyl(diam,4,x,16,-1))
for x,diam in zip([-29,0,29],[13.4,14.0,14.6]): tolcuts.append(cyl(diam,4,x,-12,-1))
tolerance=cut(tolerance,tolcuts)
# Each nut test block exactly replicates top-corner nut retention/bridge geometry.
nut_coupon=box(68,16,1.6)
for x,af in zip([-24,0,24],[5.1,5.4,5.7]):
    b=box(14,14,10,x,0,1.6)
    b=cut(b,[side_nut_pocket(x,0,1.6+4,width=af,height=3.2,direction=-1,reach=8),cyl(2.9,11,x,0,1.6)])
    nut_coupon=add([nut_coupon,b])

parts={'01_tolerance_coupon':(tolerance,1),'02_nut_coupon':(nut_coupon,1),
       '03_tft_fit_coupon':(tft_coupon,1),'04_input_fit_coupon':(input_coupon,1),
       '10_base':(base,1),'11_lid':(lid_print,1),'12_tft_spacer_3mm':(spacer,4)}
def tm(solid):
    a=solid.to_mesh()
    return trimesh.Trimesh(vertices=np.array(a.vert_properties)[:,:3],faces=np.array(a.tri_verts),process=True)
manifest=[]
for name,(solid,qty) in parts.items():
    mesh=tm(solid); mesh.apply_translation([0,0,-mesh.bounds[0,2]])
    assert mesh.is_watertight and mesh.is_winding_consistent and mesh.volume>0,name
    assert len(mesh.split())==1,name
    assert np.max(mesh.extents)<180,name
    stl=OUT/'stl'/f'{name}.stl'; mesh.export(stl)
    # Standard 3MF is a geometry container, NOT a printer preset or sliced job.
    (OUT/'3mf'/f'{name}.3mf').write_bytes(trimesh.exchange.threemf.export_3MF(trimesh.Scene(mesh)))
    manifest.append(dict(file=f'stl/{name}.stl',quantity=qty,bounds_mm=np.round(mesh.extents,4).tolist(),
        volume_cm3=round(mesh.volume/1000,3),triangles=len(mesh.faces),watertight=True,components=1,
        sha256=hashlib.sha256(stl.read_bytes()).hexdigest()))

mounts=[]
for group,pts,z in [('CASE',corners,H),('TFT',tft_holes,H-3),('ENC',encoder_holes,H-8),('PERMA',perma_holes,F+P['perma_post'])]:
    for i,(x,y) in enumerate(pts,1): mounts.append([group,str(i),x,y,z,2.9])
for b in buttons:
    for i,(x,y) in enumerate(b['holes'],1): mounts.append([b['id'],str(i),x,y,H-8,2.9])
with (OUT/'source/mount-coordinates-mm.csv').open('w',newline='',encoding='utf-8-sig') as f:
    w=csv.writer(f);w.writerow(['group','point','x_mm','y_mm','reference_z_mm','printed_clearance_d_mm']);w.writerows(mounts)
(OUT/'source/parameters.json').write_text(json.dumps(P,indent=2),encoding='utf8')
(OUT/'qa/mesh-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf8')

# Mechanical preview geometry: exact XY board outline/holes, approximate Z envelopes.
geoms=[]
def preview(name,s,c,kind='hardware',opacity=1):
    mesh=tm(s)
    geoms.append(dict(name=name,color=c,kind=kind,opacity=opacity,
        positions=np.round(mesh.vertices,4).ravel().tolist(),indices=mesh.faces.ravel().tolist()))
preview('Base',base,'#becad5','base')
preview('Lid',lid,'#e4eaf0','lid',.8)
def pcb(name,cx,cy,w,d,z,pts,color):
    solid=cut(rr(w,d,1.6,.6,cx,cy,z),holes_at(pts,z-1,4,d=3.2))
    preview(name,solid,color)
pcb('TFT PCB',tx,ty,64.77,52.578,H-4.6,tft_holes,'#1c343b')
preview('LCD glass nominal',box(60.26,42.72,3.5,tx-1.135,ty,H-3),'#182931')
preview('LCD active area approximate',box(48.96,36.72,.05,tx+1.99,ty+.05,H+.51),'#35c7d0')
fc=(tx+16.764,ty+.889)
pcb('nRF52840 approximate Z',*fc,22.86,50.8,H-19,[], '#2374a8')
preview('USB keep-clear (not printed)',box(36,22,35,P['service_center_x'],78,22),'#f49c43','clearance',.25)
preview('TFT plug/wire allowance (not printed)',box(33,40,12,tx+15,ty,H-35),'#f49c43','clearance',.25)
preview('Right-side wire passage (not printed)',box(16,100,27,66,-15,15),'#f49c43','clearance',.20)
preview('TFT ON/OFF access (not printed)',box(24,26,18,-69,50,39),'#f49c43','clearance',.20)
for b in buttons:
    x,y=b['pcb_center'];ax,ay=b['cap'];ph=b['holes']
    pcb(b['id']+' PCB',x,y,27.8,20.8,H-9.6,ph,'#2167aa')
    preview(b['id']+' cap',cyl(12.8,12.53,ax,ay,H-8),'#2185d0')
    preview(b['id']+' PH3 plug allowance',box(18,10,7,ax+25,ay,H-8),'#d9ceb0')
pcb('Encoder PCB',ex,ey,25.4,25.4,H-9.6,encoder_holes,'#172a30')
preview('Encoder body nominal envelope',box(14,14,10,ex,ey,H-8),'#a3a7a4')
preview('User knob approximate envelope',cyl(23,18,ex,ey,H+7),'#adbcc5')
pcb('Perma PCB',px,py,50.8,81.28,F+P['perma_post'],perma_holes,'#f1ead8')
preview('Perma terminal allowance',box(12,78,20,px+19,py,F+P['perma_post']+1.6),'#433933')
preview('Battery nominal 34x62x5',box(34,62,5,bx,by,F),'#f2d077')
for i,(x,y) in enumerate(tft_holes): preview('TFT spacer',spacer.translate([x,y,H-3]),'#f2eee7')
for x,y in button_holes+encoder_holes: preview('FF8',cut(cyl(5.7,8,x,y,H-8,n=6),[cyl(2.5,10,x,y,H-9)]),'#fff4d5')
(OUT/'preview/scene.json').write_text(json.dumps(dict(parameters=P,buttons=buttons,geometries=geoms),separators=(',',':')),encoding='utf8')
scene=trimesh.Scene()
for g in geoms:
    if g['kind']=='clearance':continue
    mesh=trimesh.Trimesh(np.array(g['positions']).reshape(-1,3),np.array(g['indices']).reshape(-1,3),process=False)
    mesh.visual.vertex_colors=np.tile(trimesh.visual.color.hex_to_rgba(g['color']),(len(mesh.vertices),1))
    scene.add_geometry(mesh,node_name=g['name'],geom_name=g['name'])
scene.export(OUT/'preview/assembly-preview.glb')
print(json.dumps({'parts':manifest,'output':str(OUT)},indent=2))
