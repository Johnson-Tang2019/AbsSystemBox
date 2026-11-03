# SPDX-License-Identifier: GPL-3.0-or-later
# Build-only dependency: Pillow==13.0.0.
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent.parent/'.tools/icon-packages'))
from PIL import Image,ImageDraw,ImageOps
import shutil,json,hashlib
root=Path(__file__).resolve().parent.parent
source=root/'artwork/app-icon-source.png'
original=Image.open(source).convert('RGBA')
# A geometric silhouette removes the exterior wall/shadow without thresholding white tools.
scale=4;mask=Image.new('L',(original.width*scale,original.height*scale),0)
draw=ImageDraw.Draw(mask);draw.rounded_rectangle(tuple(v*scale for v in (111,112,1143,1151)),radius=300*scale,fill=255)
mask=mask.resize(original.size,Image.Resampling.LANCZOS)
result=original.copy();result.putalpha(mask)
assets=root/'src/SysBox.App/Assets';result.save(assets/'AppIcon.png',optimize=True)
result.save(assets/'AppIcon.ico',format='ICO',sizes=[(v,v) for v in [16,20,24,32,40,48,64,96,128,256]])
for path in assets.glob('*.png'):
    if path.name=='AppIcon.png':continue
    with Image.open(path) as old:size=old.size
    canvas=Image.new('RGBA',size,(0,0,0,0));icon=ImageOps.contain(result,size,Image.Resampling.LANCZOS)
    canvas.alpha_composite(icon,((size[0]-icon.width)//2,(size[1]-icon.height)//2));canvas.save(path,optimize=True)
out=root/'artifacts/icon-review';out.mkdir(parents=True,exist_ok=True)
preview=Image.new('RGB',(1200,640),'#25232a')
for index,color in enumerate(['#25232a','#ffffff']):
    panel=Image.new('RGBA',(600,640),color);small=ImageOps.contain(result,(500,500),Image.Resampling.LANCZOS);panel.alpha_composite(small,((600-small.width)//2,24))
    x=30
    for size in [16,24,32,48,64,96]:
        icon=result.resize((size,size),Image.Resampling.LANCZOS);panel.alpha_composite(icon,(x,555));x+=size+20
    preview.paste(panel.convert('RGB'),(index*600,0))
preview.save(out/'background-and-sizes.png')
assert mask.getpixel((0,0))==0 and mask.getpixel((1253,1253))==0
assert mask.getpixel((620,600))==255
inside=0
for old,new in zip(original.get_flattened_data(),result.get_flattened_data()):
    if new[3]==255:assert old[:3]==new[:3];inside+=1
with Image.open(assets/'AppIcon.ico') as ico:
    frames=sorted(ico.ico.sizes());assert (16,16) in frames and (256,256) in frames
record={'source':'artwork/app-icon-source.png','sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),'method':'User-approved local geometric alpha mask; no thresholding or recoloring','maskBounds':[111,112,1143,1151],'cornerRadius':300,'supersampling':scale,'opaquePixelsPreservedExactly':inside,'pngMode':result.mode,'transparentCorners':True,'icoSizes':frames}
(out/'validation.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(json.dumps(record))
