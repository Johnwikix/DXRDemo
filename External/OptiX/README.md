# OptiX 9.1.0 build dependency

Headers are restored from NVIDIA's official `optix-dev` repository at
`f1f6dd803f3159992d248178f6e09421c6eb8b6d` by `Restore.ps1`. The downloaded include
directory is ignored; proprietary SDK source headers are not distributed in this
repository or in the application. See `LICENSE.txt` and `license_info.txt`.

Building the optional adapter requires CUDA driver development headers through
`CUDA_PATH/include/cuda.h`. Without them the bridge builds an explicit unavailable
adapter. CUDA 11.8 headers suffice: no CUDA kernels or CUDA runtime are linked.
Runtime loads `nvcuda.dll` and OptiX from the installed NVIDIA driver, matches its
adapter LUID to D3D12, and reports driver/device errors through the existing status.
The application does not redistribute NVIDIA driver DLLs.
