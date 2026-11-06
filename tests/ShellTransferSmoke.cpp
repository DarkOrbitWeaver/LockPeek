// Targeted B2 regression: call the production transfer synchronously so its
// HRESULT (rather than asynchronous Invoke's scheduling result) is asserted.
#ifdef UNICODE
#undef UNICODE
#endif
#include "../src/LockPeek.Shell/LockPeek.Shell.cpp"
#include <iostream>

int wmain(int argc, wchar_t **argv)
{
    if (argc != 3) return 2; // Matching staging DLL path, disposable filesystem path (Windows separators).
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return 3;
    // Use the matching package staging directory. Direct DLL mapping from
    // WindowsApps can be denied outside package activation.
    module = LoadLibraryW(argv[1]);
    if (!module) { std::wcerr << L"Module path error: " << GetLastError() << L"\n"; return 4; }
    PIDLIST_ABSOLUTE ids[2]{};
    HRESULT hr = SHParseDisplayName(argv[2], nullptr, &ids[0], 0, nullptr);
    std::wcout << L"File parse: " << std::hex << hr << L"\n";
    if (SUCCEEDED(hr))
        hr = SHParseDisplayName(L"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", nullptr, &ids[1], 0, nullptr);
    std::wcout << L"Virtual parse: " << std::hex << hr << L"\n";
    IShellItemArray *items = nullptr;
    if (SUCCEEDED(hr)) hr = SHCreateShellItemArrayFromIDLists(2, const_cast<PCIDLIST_ABSOLUTE *>(ids), &items);
    std::wcout << L"Array creation: " << std::hex << hr << L"\n";
    if (SUCCEEDED(hr))
    {
        IShellItem *last = nullptr;
        hr = items->GetItemAt(1, &last);
        if (SUCCEEDED(hr))
        {
            PWSTR path = nullptr;
            HRESULT virtualResult = last->GetDisplayName(SIGDN_FILESYSPATH, &path);
            CoTaskMemFree(path);
            last->Release();
            hr = FAILED(virtualResult) ? Transfer(items, false) : E_UNEXPECTED;
        }
    }
    if (items) items->Release();
    for (auto id : ids) CoTaskMemFree(id);
    FreeLibrary(module);
    CoUninitialize();
    std::wcout << L"Mixed filesystem + final virtual item Transfer HRESULT: 0x" << std::hex << hr << L"\n";
    return FAILED(hr) ? 1 : 0;
}
