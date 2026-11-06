#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#include <iostream>
#include <shlobj.h>
#include <shobjidl.h>
#include <vector>
#include <windows.h>
static const CLSID CommandId = {0x70c8261e, 0x55f0, 0x41d8, {0xb8, 0xd0, 0x6e, 0x22, 0xb0, 0x75, 0x32, 0x3a}};
int wmain(int argc, wchar_t **argv)
{
    if (argc < 3)
        return 2;
    bool metadata = wcscmp(argv[2], L"--metadata") == 0;
    int first = metadata ? 3 : 2;
    if (argc <= first)
        return 2;
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool recycle = wcscmp(argv[1], L"--registered-recycle") == 0;
    bool registered = recycle || wcscmp(argv[1], L"--registered") == 0;
    HMODULE dll = nullptr;
    IExplorerCommand *command = nullptr;
    HRESULT hr;
    if (registered)
    {
        CLSID selectedCommand = CommandId;
        if (recycle)
            selectedCommand.Data1 = 0x70c8261f;
        hr = CoCreateInstance(selectedCommand, nullptr, CLSCTX_LOCAL_SERVER, IID_IExplorerCommand,
                              reinterpret_cast<void **>(&command));
        if (FAILED(hr))
        {
            std::wcerr << L"CoCreateInstance " << std::hex << hr;
            return 4;
        }
    }
    else
    {
        dll = LoadLibraryW(argv[1]);
        if (!dll)
            return 3;
        auto get =
            reinterpret_cast<HRESULT(WINAPI *)(REFCLSID, REFIID, void **)>(GetProcAddress(dll, "DllGetClassObject"));
        IClassFactory *factory = nullptr;
        hr = get(CommandId, IID_IClassFactory, reinterpret_cast<void **>(&factory));
        if (FAILED(hr))
            return 4;
        hr = factory->CreateInstance(nullptr, IID_IExplorerCommand, reinterpret_cast<void **>(&command));
        factory->Release();
        if (FAILED(hr))
            return 5;
    }
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (int i = first; i < argc; i++)
    {
        PIDLIST_ABSOLUTE id = nullptr;
        hr = SHParseDisplayName(argv[i], nullptr, &id, 0, nullptr);
        if (FAILED(hr))
        {
            std::wcerr << L"Parse " << std::hex << hr;
            return 6;
        }
        ids.push_back(id);
    }
    IShellItemArray *items = nullptr;
    hr = SHCreateShellItemArrayFromIDLists((UINT)ids.size(), const_cast<PCIDLIST_ABSOLUTE *>(ids.data()), &items);
    if (FAILED(hr))
        return 7;
    PWSTR title = nullptr;
    hr = command->GetTitle(items, &title);
    if (FAILED(hr) || !title)
        return 9;
    std::wcout << L"Title: " << title << L"\n";
    CoTaskMemFree(title);
    if (metadata)
    {
        items->Release();
        command->Release();
        for (auto id : ids)
            CoTaskMemFree(id);
        if (dll)
            FreeLibrary(dll);
        CoUninitialize();
        return 0;
    }
    auto start = GetTickCount64();
    hr = command->Invoke(items, nullptr);
    std::wcout << L"Invoke returned " << std::hex << hr << std::dec << L" in " << (GetTickCount64() - start)
               << L" ms for " << ids.size() << L" files\n";
    items->Release();
    command->Release();
    for (auto id : ids)
        CoTaskMemFree(id);
    // Pump STA messages so the worker can unmarshal IShellItemArray.
    if (registered)
    {
        while (GetTickCount64() - start < 5000)
        {
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            Sleep(10);
        }
        CoUninitialize();
        return FAILED(hr) ? 1 : 0;
    }
    auto unload = reinterpret_cast<HRESULT(WINAPI *)()>(GetProcAddress(dll, "DllCanUnloadNow"));
    while (unload() != S_OK && GetTickCount64() - start < 35000)
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        Sleep(10);
    }
    if (unload() != S_OK)
    {
        std::wcerr << L"worker did not complete";
        return 8;
    }
    FreeLibrary(dll);
    CoUninitialize();
    return FAILED(hr) ? 1 : 0;
}
