# Model attribution

Source: [Stanford University Computer Graphics Laboratory — 3D Scanning Repository](https://graphics.stanford.edu/data/3Dscanrep/).

These models are used in the local path-tracing demos and their independent research benchmark. The repository permits research use and free redistribution with acknowledgement; commercial use requires permission. They are not presented here as CC0 assets. Refer to the source page for the full terms.

| Archive | Official URL | SHA-256 |
|---|---|---|
| Bunny | https://graphics.stanford.edu/pub/3Dscanrep/bunny.tar.gz | a5720bd96d158df403d153381b8411a727a1d73cff2f33dc9b212d6f75455b84 |
| Armadillo | https://graphics.stanford.edu/pub/3Dscanrep/armadillo/Armadillo.ply.gz | 8b9b56cc36e66d54429b1e1e75bd89e833645bfe0dc7c1afd1205877a7356a3f |
| Dragon | https://graphics.stanford.edu/pub/3Dscanrep/dragon/dragon_recon.tar.gz | 74ac1d90989c9b1732edee82d57e9ce71452144cf4355f108d8c9c616d28d02f |

Actual downloaded PLY face counts are used in results: Bunny 69,451; Armadillo 345,944; Dragon LODs 11,102 / 47,794 / 202,520 / 871,414. The source page currently lists a different full Dragon reconstruction count; this benchmark reports the actual archive contents, not the page's summary.

Only translation and uniform scaling are applied: center X/Z, rest the model on Y=0, scale the largest bounding-box dimension to 2. The topology, winding and number of triangles are preserved. Faces are reordered for the software BVH, and the identical reordered triangles feed the DXR BLAS. No model decimation is performed by this benchmark.
