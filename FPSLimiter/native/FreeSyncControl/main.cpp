// Optional AMD ADLX companion for FPS Limiter.
// Build this as a 64-bit Windows C++ console program with AMD's official ADLX SDK.
// Intentionally refuses multiple displays: Playnite must not disable the wrong monitor.
// stdout contract: `state` emits only 0 or 1 on success; `on`/`off` emits OK.
// Failure returns nonzero; no fallback to undocumented registry/settings writes.
#include "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.h"
#include "SDK/Include/IDisplays.h"
#include "SDK/Include/IDisplaySettings.h"
#include <iostream>
#include <string>
using namespace adlx;

int main(int argc, char** argv)
{
    if (argc != 2) return 2;
    std::string command(argv[1]);
    if (command != "state" && command != "on" && command != "off") return 2;
    ADLXHelper helper;
    ADLX_RESULT res = helper.Initialize();
    if (ADLX_FAILED(res)) return 3;
    int result = 3;
    {
    do
    {
        auto system = helper.GetSystemServices();
        if (system == nullptr) break;
        IADLXDisplayServicesPtr displayService;
        res = system->GetDisplaysServices(&displayService);
        if (ADLX_FAILED(res) || displayService == nullptr) break;
        IADLXDisplayListPtr displays;
        res = displayService->GetDisplays(&displays);
        if (ADLX_FAILED(res) || displays == nullptr) break;
        // Fail closed on multiple AMD displays until display-ID mapping is implemented.
        if ((displays->End() - displays->Begin()) != 1) { result = 4; break; }
        IADLXDisplayPtr display;
        res = displays->At(displays->Begin(), &display);
        if (ADLX_FAILED(res) || display == nullptr) break;
        IADLXDisplayFreeSyncPtr freeSync;
        res = displayService->GetFreeSync(display, &freeSync);
        if (ADLX_FAILED(res) || freeSync == nullptr) break;
        adlx_bool supported = false;
        res = freeSync->IsSupported(&supported);
        if (ADLX_FAILED(res) || !supported) { result = 5; break; }
        adlx_bool enabled = false;
        res = freeSync->IsEnabled(&enabled);
        if (ADLX_FAILED(res)) break;
        if (command == "state")
        {
            std::cout << (enabled ? "1" : "0") << std::endl;
            result = 0;
            break;
        }
        adlx_bool target = command == "on";
        if (enabled != target)
        {
            res = freeSync->SetEnabled(target);
            if (ADLX_FAILED(res)) break;
            // Driver accepted a request; confirm it actually took effect.
            res = freeSync->IsEnabled(&enabled);
            if (ADLX_FAILED(res) || enabled != target) break;
        }
        std::cout << "OK" << std::endl;
        result = 0;
    } while (false);
    } // Release ADLX smart pointers before unloading driver DLL.
    helper.Terminate();
    return result;
}
