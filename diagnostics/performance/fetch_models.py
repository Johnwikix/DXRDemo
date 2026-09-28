"""Download Stanford research models; retain attribution in models/SOURCES.md."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import gzip
import hashlib
import tarfile
import urllib.request

root=Path(__file__).resolve().parent/'models'
root.mkdir(exist_ok=True)
urls={
 'bunny.tar.gz':'https://graphics.stanford.edu/pub/3Dscanrep/bunny.tar.gz',
 'Armadillo.ply.gz':'https://graphics.stanford.edu/pub/3Dscanrep/armadillo/Armadillo.ply.gz',
 'dragon_recon.tar.gz':'https://graphics.stanford.edu/pub/3Dscanrep/dragon/dragon_recon.tar.gz',
}
def fetch(item):
    name,url=item
    dest=root/name
    if not dest.exists():
        request=urllib.request.Request(url,headers={'User-Agent':'DXRDemo-research-benchmark/1.0'})
        with urllib.request.urlopen(request,timeout=60) as response:
            dest.write_bytes(response.read())
    print(name, dest.stat().st_size, 'sha256='+hashlib.sha256(dest.read_bytes()).hexdigest(), flush=True)
    if name.endswith('.tar.gz'):
        with tarfile.open(dest) as archive:
            for member in archive.getmembers():
                if member.isfile() and member.name.endswith('.ply') and ('reconstruction' in member.name or 'vrip' in member.name):
                    # Only read selected regular-file bytes. Never extract archive paths.
                    (root/Path(member.name).name).write_bytes(archive.extractfile(member).read())
                    print('extracted',member.name,flush=True)
    else:
        (root/'Armadillo.ply').write_bytes(gzip.decompress(dest.read_bytes()))
with ThreadPoolExecutor(max_workers=3) as pool:
    list(pool.map(fetch,urls.items()))
