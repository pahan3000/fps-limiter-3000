# Automatic AMD ADLX setup (FreeSync control)

**No manual AMD ADLX download is needed.** Build `FPSLimiter.csproj` in **Release** in Visual Studio. MSBuild runs `native/FreeSyncControl/build-auto.ps1` automatically, which downloads the public AMD ADLX SDK source from the official GPUOpen GitHub repository (first time only), compiles `FreeSyncControl.exe` with the Visual Studio x64 C++ compiler, and places the EXE beside `FPSLimiter.dll`.

Prerequisites: Windows, an AMD GPU/driver supported by ADLX, Visual Studio with **Desktop development with C++** workload and a Windows SDK, and network access during the first build. No extra ADLX installation or manual helper copying is required. Build fails with an actionable error if those tools aren't present. The AMD SDK cache is `%LOCALAPPDATA%\FPSLimiter\adlx-build`.

**Packaging:** Ensure `FreeSyncControl.exe` is included next to `FPSLimiter.dll` in the finished `.pext`. If you use Playnite Toolbox, verify that the EXE is included in the archive; the plain project build doesn't itself create a `.pext`. FreeSync changes work only if the helper is packaged and the AMD driver reports support.

**Safety:** Scanline and the per-game toggle use the existing original-state restoration logic. Unclean Playnite termination can still leave FreeSync disabled; re-enable in AMD Software if necessary. Current native helper declines multiple ADLX displays instead of choosing the wrong one. The toggle cannot function on unsupported GPU drivers.

ADLX official sources: https://github.com/GPUOpen-LibrariesAndSDKs/ADLX ; https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/display/iadlxdisplayfreesync/setenabled/ . The SDK is downloaded directly from AMD's repository, not bundled or downloaded from a third-party binary site. Review the AMD ADLX SDK license before redistributing compiled components.
