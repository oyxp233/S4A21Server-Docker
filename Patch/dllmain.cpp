#include <windows.h>
#include "86JP.h"

#define EXPAPI(Name) EXTERN_C __declspec(dllexport) void Name() {}

EXPAPI(A)

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        JPEntry();
        break;
    }
    return TRUE;
}
