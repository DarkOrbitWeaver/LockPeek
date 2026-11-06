#define UNICODE
#define _UNICODE
#include <atomic>
#include <new>
#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <string>
#include <vector>
#include <windows.h>

// All scanning and process actions live in C#. This DLL only transfers the selection.
static const CLSID CommandId = {0x70c8261e, 0x55f0, 0x41d8, {0xb8, 0xd0, 0x6e, 0x22, 0xb0, 0x75, 0x32, 0x3a}};
static const CLSID RecycleCommandId = {0x70c8261f, 0x55f0, 0x41d8, {0xb8, 0xd0, 0x6e, 0x22, 0xb0, 0x75, 0x32, 0x3a}};
static HMODULE module;
static std::atomic<long> objects{0};
struct Handle
{
    HANDLE h = nullptr;
    ~Handle()
    {
        if (h && h != INVALID_HANDLE_VALUE)
            CloseHandle(h);
    }
};
static std::wstring ModuleDirectory()
{
    std::vector<wchar_t> path(32768);
    DWORD count = GetModuleFileNameW(module, path.data(), (DWORD)path.size());
    if (!count || count >= path.size())
        return L"";
    std::wstring result(path.data(), count);
    return result.substr(0, result.find_last_of(L"\\") + 1);
}
static void Append(std::vector<BYTE> &bytes, DWORD value)
{
    const auto *ptr = reinterpret_cast<const BYTE *>(&value);
    bytes.insert(bytes.end(), ptr, ptr + 4);
}
struct Watchdog
{
    HANDLE thread, done;
};
static DWORD WINAPI Deadline(void *data)
{
    auto *d = static_cast<Watchdog *>(data);
    if (WaitForSingleObject(d->done, 30000) == WAIT_TIMEOUT)
        CancelSynchronousIo(d->thread);
    CloseHandle(d->thread);
    CloseHandle(d->done);
    delete d;
    return 0;
}
static void Trace(const wchar_t *stage, HRESULT hr)
{
    if (SUCCEEDED(hr))
        return;
    wchar_t folder[MAX_PATH];
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, folder)))
        return;
    auto dir = std::wstring(folder) + L"\\LockPeek";
    CreateDirectoryW(dir.c_str(), nullptr);
    Handle log;
    log.h = CreateFileW((dir + L"\\shell-launch.log").c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (log.h == INVALID_HANDLE_VALUE)
        return;
    LARGE_INTEGER length{};
    if (GetFileSizeEx(log.h, &length) && length.QuadPart > 65536)
        SetEndOfFile(log.h);
    SetFilePointer(log.h, 0, nullptr, FILE_END);
    wchar_t line[160];
    int chars = swprintf_s(line, L"%ls: 0x%08lX\r\n", stage, static_cast<unsigned long>(hr));
    DWORD written;
    WriteFile(log.h, line, chars * sizeof(wchar_t), &written, nullptr);
}
static HRESULT Transfer(IShellItemArray *items, bool recycle)
{
    DWORD count = 0;
    HRESULT hr = items->GetCount(&count);
    if (FAILED(hr))
        return hr;
    std::vector<BYTE> bytes;
    bytes.reserve(16384);
    Append(bytes, 0x324B504C);
    Append(bytes, 0);
    Append(bytes, 0);
    Append(bytes, recycle ? 1 : 0);
    DWORD accepted = 0, skipped = 0;
    for (DWORD i = 0; i < count; ++i)
    {
        if (i >= 100000)
        {
            skipped = count - i + skipped;
            break;
        }
        IShellItem *item = nullptr;
        PWSTR path = nullptr;
        hr = items->GetItemAt(i, &item);
        if (SUCCEEDED(hr))
        {
            hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
            item->Release();
        }
        if (FAILED(hr) || !path)
        {
            ++skipped;
            continue;
        }
        size_t len = wcslen(path);
        if (len == 0 || len > 32767 || bytes.size() + 4 + len * 2 > 16 * 1024 * 1024)
        {
            ++skipped;
            CoTaskMemFree(path);
            continue;
        }
        Append(bytes, (DWORD)len);
        const auto *ptr = reinterpret_cast<BYTE *>(path);
        bytes.insert(bytes.end(), ptr, ptr + len * 2);
        ++accepted;
        CoTaskMemFree(path);
    }
    memcpy(bytes.data() + 4, &accepted, 4);
    memcpy(bytes.data() + 8, &skipped, 4);
    SECURITY_ATTRIBUTES sa{sizeof(sa), nullptr, TRUE};
    Handle reader, writer;
    if (!CreatePipe(&reader.h, &writer.h, &sa, 65536))
        return HRESULT_FROM_WIN32(GetLastError());
    SetHandleInformation(writer.h, HANDLE_FLAG_INHERIT, 0);
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    std::vector<BYTE> attributes(size);
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
    if (!InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &size))
        return HRESULT_FROM_WIN32(GetLastError());
    if (!UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &reader.h,
                                   sizeof(HANDLE), nullptr, nullptr))
    {
        DeleteProcThreadAttributeList(startup.lpAttributeList);
        return HRESULT_FROM_WIN32(GetLastError());
    }
    auto exe = ModuleDirectory() + L"LockPeek.exe";
    auto command = L"\"" + exe + L"\" --selection-pipe " + std::to_wstring(reinterpret_cast<UINT_PTR>(reader.h));
    PROCESS_INFORMATION pi{};
    BOOL launched = CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE,
                                   EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, nullptr,
                                   ModuleDirectory().c_str(), &startup.StartupInfo, &pi);
    DWORD error = GetLastError();
    DeleteProcThreadAttributeList(startup.lpAttributeList);
    Trace(L"CreateProcess", launched ? S_OK : HRESULT_FROM_WIN32(error));
    if (!launched)
        return HRESULT_FROM_WIN32(error);
    AllowSetForegroundWindow(pi.dwProcessId);
    Handle child;
    child.h = pi.hProcess;
    CloseHandle(pi.hThread);
    CloseHandle(reader.h);
    reader.h = nullptr;
    auto *deadline = new (std::nothrow) Watchdog{};
    Handle done;
    if (!deadline)
        return E_OUTOFMEMORY;
    done.h = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &deadline->thread, 0, FALSE,
                    DUPLICATE_SAME_ACCESS);
    DuplicateHandle(GetCurrentProcess(), done.h, GetCurrentProcess(), &deadline->done, 0, FALSE, DUPLICATE_SAME_ACCESS);
    Handle timer;
    timer.h = CreateThread(nullptr, 0, Deadline, deadline, 0, nullptr);
    if (!timer.h)
    {
        CloseHandle(deadline->thread);
        CloseHandle(deadline->done);
        delete deadline;
        return E_FAIL;
    }
    size_t offset = 0;
    hr = S_OK;
    while (offset < bytes.size())
    {
        DWORD wrote = 0;
        if (!WriteFile(writer.h, bytes.data() + offset, (DWORD)(bytes.size() - offset), &wrote, nullptr) || wrote == 0)
        {
            DWORD writeError = GetLastError();
            hr = HRESULT_FROM_WIN32(writeError ? writeError : ERROR_WRITE_FAULT);
            break;
        }
        offset += wrote;
    }
    SetEvent(done.h);
    WaitForSingleObject(timer.h, INFINITE);
    CloseHandle(writer.h);
    writer.h = nullptr;
    return hr;
}
struct SelectionWork
{
    IStream *stream;
    bool recycle;
};
static DWORD WINAPI SelectionWorker(void *workPointer)
{
    auto *work = static_cast<SelectionWork *>(workPointer);
    IStream *marshalled = work->stream;
    bool recycle = work->recycle;
    delete work;
    Trace(L"Worker started", S_OK);
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&SelectionWorker), &pinned);
    HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IShellItemArray *items = nullptr;
    HRESULT hr = CoGetInterfaceAndReleaseStream(marshalled, IID_IShellItemArray, reinterpret_cast<void **>(&items));
    if (SUCCEEDED(hr))
    {
        Trace(L"Selection unmarshalled", S_OK);
        try
        {
            hr = Transfer(items, recycle);
        }
        catch (...)
        {
            hr = E_FAIL;
        }
        items->Release();
    }
    Trace(L"Transfer completed", hr);
    if (FAILED(hr))
    {
        wchar_t message[256];
        swprintf_s(message, L"LockPeek could not open the selected items (error 0x%08lX). Please try again.",
                   static_cast<unsigned long>(hr));
        MessageBoxW(nullptr, message, L"LockPeek", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    }
    if (SUCCEEDED(initialized))
        CoUninitialize();
    --objects;
    CoReleaseServerProcess();
    if (pinned)
        FreeLibraryAndExitThread(pinned, 0);
    return 0;
}
class Command final : public IExplorerCommand
{
    std::atomic<ULONG> refs{1};

  public:
    bool recycle;
    Command(bool recycleMode) : recycle(recycleMode)
    {
        ++objects;
    }
    ~Command()
    {
        --objects;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **result) override
    {
        *result = nullptr;
        if (id == IID_IUnknown || id == IID_IExplorerCommand)
        {
            *result = static_cast<IExplorerCommand *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++refs;
    }
    ULONG STDMETHODCALLTYPE Release() override
    {
        auto count = --refs;
        if (!count)
            delete this;
        return count;
    }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray *items, PWSTR *title) override
    {
        if (recycle)
            return SHStrDupW(L"Check and delete...", title);
        DWORD count = 0;
        SFGAOF attributes = 0;
        if (items && SUCCEEDED(items->GetCount(&count)) && count == 1)
        {
            items->GetAttributes(SIATTRIBFLAGS_AND, SFGAO_FOLDER, &attributes);
            return SHStrDupW(
                (attributes & SFGAO_FOLDER) ? L"Find apps using this folder" : L"Find apps using this file", title);
        }
        return SHStrDupW(L"Find apps using these items", title);
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray *, PWSTR *icon) override
    {
        DWORD light = 1, size = sizeof(light);
        RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                     L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &light, &size);
        if (recycle)
            return SHStrDupW((ModuleDirectory() + L"assets\\context-menu\\recycle.ico").c_str(), icon);
        auto path = ModuleDirectory() +
                    (light ? L"assets\\context-menu\\lockpeek-light.ico" : L"assets\\context-menu\\lockpeek-dark.ico");
        return SHStrDupW(path.c_str(), icon);
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray *, PWSTR *tip) override
    {
        return SHStrDupW(recycle ? L"Scan first, then review selected items before deleting"
                                 : L"Find apps using the selected files or folders",
                         tip);
    }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID *id) override
    {
        *id = recycle ? RecycleCommandId : CommandId;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray *items, BOOL, EXPCMDSTATE *state) override
    {
        if (!state)
            return E_POINTER;
        *state = ECS_HIDDEN;
        if (!items)
            return S_OK;

        DWORD count = 0;
        if (FAILED(items->GetCount(&count)) || count == 0)
            return S_OK;

        IShellItem *first = nullptr;
        if (SUCCEEDED(items->GetItemAt(0, &first)) && first)
        {
            SFGAOF attrs = 0;
            if (SUCCEEDED(first->GetAttributes(SFGAO_FILESYSTEM, &attrs)) && (attrs & SFGAO_FILESYSTEM))
            {
                *state = ECS_ENABLED;
            }
            first->Release();
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray *items, IBindCtx *) override
    {
        if (!items)
            return E_INVALIDARG;
        IStream *stream = nullptr;
        HRESULT hr = CoMarshalInterThreadInterfaceInStream(IID_IShellItemArray, items, &stream);
        if (FAILED(hr))
            return hr;
        Trace(L"Invoke marshalled", hr);
        CoAddRefServerProcess();
        ++objects;
        Handle worker;
        auto *work = new (std::nothrow) SelectionWork{stream, recycle};
        if (!work)
        {
            --objects;
            CoReleaseServerProcess();
            stream->Release();
            return E_OUTOFMEMORY;
        }
        worker.h = CreateThread(nullptr, 0, SelectionWorker, work, 0, nullptr);
        if (!worker.h)
        {
            CoReleaseServerProcess();
            --objects;
            delete work;
            stream->Release();
            return HRESULT_FROM_WIN32(GetLastError());
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS *flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand **result) override
    {
        *result = nullptr;
        return E_NOTIMPL;
    }
};
class Factory final : public IClassFactory
{
    std::atomic<ULONG> refs{1};

  public:
    bool recycle;
    Factory(bool recycleMode) : recycle(recycleMode)
    {
        ++objects;
    }
    ~Factory()
    {
        --objects;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **result) override
    {
        *result = nullptr;
        if (id == IID_IUnknown || id == IID_IClassFactory)
        {
            *result = static_cast<IClassFactory *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++refs;
    }
    ULONG STDMETHODCALLTYPE Release() override
    {
        auto n = --refs;
        if (!n)
            delete this;
        return n;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown *outer, REFIID id, void **result) override
    {
        if (outer)
            return CLASS_E_NOAGGREGATION;
        auto *cmd = new (std::nothrow) Command(recycle);
        if (!cmd)
            return E_OUTOFMEMORY;
        auto hr = cmd->QueryInterface(id, result);
        cmd->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        objects += lock ? 1 : -1;
        return S_OK;
    }
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID cls, REFIID iid, void **result)
{
    *result = nullptr;
    if (cls != CommandId && cls != RecycleCommandId)
        return CLASS_E_CLASSNOTAVAILABLE;
    auto *factory = new (std::nothrow) Factory(cls == RecycleCommandId);
    if (!factory)
        return E_OUTOFMEMORY;
    auto hr = factory->QueryInterface(iid, result);
    factory->Release();
    return hr;
}
extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return objects == 0 ? S_OK : S_FALSE;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
