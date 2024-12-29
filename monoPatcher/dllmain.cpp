#include "pch.h"

DWORD* gc_disabled = nullptr;

typedef void(WINAPI* gcEnableCloseCallType)();
gcEnableCloseCallType pfnGcDisable = nullptr;
gcEnableCloseCallType pfnGcEnable = nullptr;

BOOL gcEnable = true;

EXTERN_C __declspec(dllexport) BOOL monoPatchInit()
{
    auto monoDll = (BYTE*)GetModuleHandle(L"mono.dll");

    if (monoDll == nullptr)
        return false;

    pfnGcEnable = (gcEnableCloseCallType)(monoDll + 0x157840);
    pfnGcDisable = (gcEnableCloseCallType)(monoDll + 0x15786C);

    if (*(BYTE*)pfnGcEnable != 0x48 && *(BYTE*)((BYTE*)pfnGcEnable + 4) != 0x48)
        return false;

    gc_disabled = (DWORD*)(monoDll + 0x2656C8);

    gcEnable = true;

    return true;
}

EXTERN_C __declspec(dllexport) BOOL monoSetGCStatus(BOOL bEnable)
{
    if (gc_disabled && pfnGcDisable && pfnGcEnable)
    {
        if (bEnable)
        {
            if (!gcEnable)
            {
                pfnGcEnable();
                //*gc_disabled = 0;
                gcEnable = true;
            }
        }
        else
        {
            if (gcEnable)
            {
                //*gc_disabled = 1;
                pfnGcDisable();

                gcEnable = false;
            }
        }

        return true;
    }

    return false;
}

BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

