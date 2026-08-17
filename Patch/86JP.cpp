#include "86JP.h"
#include "HookInterface.h"
#include "XLog.h"

#include <mutex>

#pragma comment(lib, "user32.lib")

static uintptr_t dnf_base = 0;

namespace
{
    constexpr uintptr_t A21_GameLog_Rva = 0x023BE270;      // IDA VA 0x027BE270，A21 GameLog入口
    constexpr uintptr_t A21_GameLogEx_Rva = 0x023BE370;    // IDA VA 0x027BE370，A21 GameLog变体入口

    std::once_flag g_StartOnce;
    std::once_flag g_HookOnce;

    const wchar_t* GameLogPath()
    {
        static wchar_t path[MAX_PATH] = {};
        if (path[0])
            return path;

        const DWORD length = GetModuleFileNameW(NULL, path, _countof(path));
        if (!length || length >= _countof(path))
        {
            wcscpy_s(path, L"GameLog.log");
            return path;
        }

        wchar_t* slash = wcsrchr(path, L'\\');
        if (!slash)
            slash = wcsrchr(path, L'/');
        if (slash)
            wcscpy_s(slash + 1, _countof(path) - (slash + 1 - path), L"GameLog.log");
        else
            wcscpy_s(path, L"GameLog.log");
        return path;
    }
}

void __cdecl ProxyGameLog(int a1, wchar_t* source_path, wchar_t* function_name, int logType, wchar_t* Format, ...)
{
    wchar_t Buffer[512] = { 0 };
    wchar_t* dynamicBuffer = NULL;
    wchar_t* outputBuffer = Buffer;
    int bufferSize = _countof(Buffer);

    va_list ArgList;
    va_start(ArgList, Format);

    int result = _vswprintf_c_l(Buffer, bufferSize, Format, 0, ArgList);

    if (result < 0) {
        va_end(ArgList);
        va_start(ArgList, Format);

        int neededSize = _vscwprintf_l(Format, 0, ArgList) + 1;

        if (neededSize > 0) {
            dynamicBuffer = (wchar_t*)malloc(neededSize * sizeof(wchar_t));
            if (dynamicBuffer) {
                va_end(ArgList);
                va_start(ArgList, Format);
                _vswprintf_c_l(dynamicBuffer, neededSize, Format, 0, ArgList);
                outputBuffer = dynamicBuffer;
            }
        }
    }

    va_end(ArgList);

    if (outputBuffer) {
        AppendFileLogFormatLine(GameLogPath(), L"[%s] [%d] [%s]", function_name, logType, outputBuffer);
    }

    if (dynamicBuffer) {
        free(dynamicBuffer);
    }
}

// A21加密入口尚未重新定位，当前保留A12实现作为后续特征参考，不安装hook。

int __fastcall Proxy_CipherEncrypt(void* This, void* NotUsed, int packet_type, char* input, int in_size, char* out_put, int* out_size)
{
    *(int*)(input - 13 + 3) = in_size + 13;

    *out_size = in_size;
    memcpy(out_put, input, in_size);
    return 1;
}

static uintptr_t g_Ptr_SendMessageW = 0;
LRESULT WINAPI Proxy_SendMessageW(HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam)
{
    if (Msg == 0x111 && wParam == 0x19F && lParam == 0)
        return 0;
    auto original = reinterpret_cast<decltype(&Proxy_SendMessageW)>(Hook_GetTrampoline(g_Ptr_SendMessageW));
    return original(hWnd, Msg, wParam, lParam);
}

unsigned int DelayHook(void*)
{
    do
    {
        dnf_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DNF.exe"));
        Sleep(100);
    } while (!dnf_base);

    do
    {
        Sleep(100);
    } while (nullptr == GetModuleHandleW(L"GameGaurd.dll"));

    Sleep(1000);

    std::call_once(g_HookOnce, []()
        {
            DeleteFileW(L"GameLog.log");
            DeleteFileW(GameLogPath());

            Hook_Inline(reinterpret_cast<void*>(dnf_base + A21_GameLog_Rva), ProxyGameLog);
            Hook_Inline(reinterpret_cast<void*>(dnf_base + A21_GameLogEx_Rva), ProxyGameLog);

            auto user32 = GetModuleHandleW(L"user32.dll");
            if (user32)
            {
                g_Ptr_SendMessageW = (uintptr_t)GetProcAddress(user32, "SendMessageW");
                Hook_Inline(reinterpret_cast<void*>(g_Ptr_SendMessageW), Proxy_SendMessageW);
            }

            AppendFileLogLine(GameLogPath(), L"[Patch] A21 GameLog hook installed.");
        });

    return 0;
}

void PluginEntry()
{
    std::call_once(g_StartOnce, []()
        {
            AppendFileLogLine(GameLogPath(), L"[Patch] 86JP.dll loaded.");

            auto thread = CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE)DelayHook, NULL, 0, NULL);
            if (thread)
                CloseHandle(thread);
        });
}

void JPEntry()
{
    PluginEntry();
}
