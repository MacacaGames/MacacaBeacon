#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <shlwapi.h>
#include <wincodec.h>
#include <d3d11.h>
#include <dxgi.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <new>
#include <string>
#include <vector>

#ifdef _MSC_VER
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "d3d11.lib")
#endif

namespace
{
    // Some MinGW-w64 SDK revisions do not declare this Windows 8+ attribute even
    // though the operating system supports it. Value from the Windows SDK mfidl.h.
    const GUID MacacaMpeg4SinkMoovBeforeMdat =
        { 0xf672e3ac, 0xe1e6, 0x4f10, { 0xb5, 0xec, 0x5f, 0x3b, 0x30, 0x82, 0x88, 0x16 } };

    template <typename T>
    void SafeRelease(T*& pointer)
    {
        if (pointer != nullptr)
        {
            pointer->Release();
            pointer = nullptr;
        }
    }

    std::wstring Utf8ToWide(const char* value)
    {
        if (value == nullptr || value[0] == '\0')
            return std::wstring();

        const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, nullptr, 0);
        if (length <= 0)
            return std::wstring();

        std::wstring result(static_cast<size_t>(length), L'\0');
        if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, &result[0], length) <= 0)
            return std::wstring();
        result.resize(static_cast<size_t>(length - 1));
        return result;
    }

    std::string HResultMessage(const char* operation, HRESULT result)
    {
        char* systemMessage = nullptr;
        const DWORD length = FormatMessageA(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr,
            static_cast<DWORD>(result),
            MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
            reinterpret_cast<char*>(&systemMessage),
            0,
            nullptr);

        std::string message(operation == nullptr ? "Media Foundation operation failed" : operation);
        message += " (HRESULT 0x";
        char code[16] = {};
        sprintf_s(code, "%08lX", static_cast<unsigned long>(result));
        message += code;
        message += ")";
        if (length > 0 && systemMessage != nullptr)
        {
            message += ": ";
            message.append(systemMessage, length);
            while (!message.empty() && (message.back() == '\r' || message.back() == '\n'))
                message.pop_back();
        }
        if (systemMessage != nullptr)
            LocalFree(systemMessage);
        return message;
    }

    struct EncoderSession
    {
        IMFSinkWriter* writer = nullptr;
        IWICImagingFactory* imagingFactory = nullptr;
        DWORD streamIndex = 0;
        int width = 0;
        int height = 0;
        int framesPerSecond = 0;
        LONGLONG frameDuration = 0;
        LONGLONG lastSampleTime = -1;
        bool mediaFoundationStarted = false;
        bool uninitializeCom = false;
        bool ready = false;
    bool finalized = false;
    volatile LONG pendingGpuEvents = 0;
    std::string lastError;

        ~EncoderSession()
        {
            SafeRelease(writer);
            SafeRelease(imagingFactory);
            if (mediaFoundationStarted)
                MFShutdown();
            if (uninitializeCom)
                CoUninitialize();
        }

        bool Fail(const char* operation, HRESULT result)
        {
            lastError = HResultMessage(operation, result);
            return false;
        }
    };

    struct GpuSubmit
    {
        EncoderSession* session = nullptr;
        void* nativeTexture = nullptr;
        double presentationSeconds = 0.0;
    };

    bool ConfigureOutput(EncoderSession* session, int bitrate)
    {
        IMFMediaType* outputType = nullptr;
        HRESULT result = MFCreateMediaType(&outputType);
        if (SUCCEEDED(result)) result = outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(result)) result = outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        if (SUCCEEDED(result)) result = outputType->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(bitrate));
        if (SUCCEEDED(result)) result = outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(result)) result = MFSetAttributeSize(outputType, MF_MT_FRAME_SIZE, session->width, session->height);
        if (SUCCEEDED(result)) result = MFSetAttributeRatio(outputType, MF_MT_FRAME_RATE, session->framesPerSecond, 1);
        if (SUCCEEDED(result)) result = MFSetAttributeRatio(outputType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(result)) result = session->writer->AddStream(outputType, &session->streamIndex);
        SafeRelease(outputType);
        return SUCCEEDED(result) || session->Fail("Could not configure the H.264 output stream", result);
    }

    bool ConfigureInput(EncoderSession* session)
    {
        IMFMediaType* inputType = nullptr;
        HRESULT result = MFCreateMediaType(&inputType);
        if (SUCCEEDED(result)) result = inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(result)) result = inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        if (SUCCEEDED(result)) result = inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(result)) result = inputType->SetUINT32(MF_MT_DEFAULT_STRIDE, static_cast<UINT32>(session->width * 4));
        if (SUCCEEDED(result)) result = MFSetAttributeSize(inputType, MF_MT_FRAME_SIZE, session->width, session->height);
        if (SUCCEEDED(result)) result = MFSetAttributeRatio(inputType, MF_MT_FRAME_RATE, session->framesPerSecond, 1);
        if (SUCCEEDED(result)) result = MFSetAttributeRatio(inputType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(result)) result = session->writer->SetInputMediaType(session->streamIndex, inputType, nullptr);
        SafeRelease(inputType);
        return SUCCEEDED(result) || session->Fail("Could not configure the RGB input stream", result);
    }

    bool DecodeJpeg(EncoderSession* session, const uint8_t* bytes, int byteCount, std::vector<uint8_t>& pixels)
    {
        IStream* stream = SHCreateMemStream(bytes, static_cast<UINT>(byteCount));
        if (stream == nullptr)
        {
            session->lastError = "Windows Imaging Component could not create a JPEG memory stream.";
            return false;
        }

        IWICBitmapDecoder* decoder = nullptr;
        IWICBitmapFrameDecode* frame = nullptr;
        IWICBitmapScaler* scaler = nullptr;
        IWICFormatConverter* converter = nullptr;
        HRESULT result = session->imagingFactory->CreateDecoderFromStream(
            stream, nullptr, WICDecodeMetadataCacheOnLoad, &decoder);
        if (SUCCEEDED(result)) result = decoder->GetFrame(0, &frame);

        UINT sourceWidth = 0;
        UINT sourceHeight = 0;
        if (SUCCEEDED(result)) result = frame->GetSize(&sourceWidth, &sourceHeight);

        IWICBitmapSource* source = frame;
        if (SUCCEEDED(result) &&
            (sourceWidth != static_cast<UINT>(session->width) || sourceHeight != static_cast<UINT>(session->height)))
        {
            result = session->imagingFactory->CreateBitmapScaler(&scaler);
            if (SUCCEEDED(result))
                result = scaler->Initialize(frame, session->width, session->height, WICBitmapInterpolationModeFant);
            source = scaler;
        }

        if (SUCCEEDED(result)) result = session->imagingFactory->CreateFormatConverter(&converter);
        if (SUCCEEDED(result))
        {
            result = converter->Initialize(
                source,
                GUID_WICPixelFormat32bppBGRA,
                WICBitmapDitherTypeNone,
                nullptr,
                0.0,
                WICBitmapPaletteTypeCustom);
        }

        const UINT stride = static_cast<UINT>(session->width * 4);
        const UINT bufferSize = stride * static_cast<UINT>(session->height);
        pixels.resize(bufferSize);
        if (SUCCEEDED(result))
            result = converter->CopyPixels(nullptr, stride, bufferSize, pixels.data());

        SafeRelease(converter);
        SafeRelease(scaler);
        SafeRelease(frame);
        SafeRelease(decoder);
        SafeRelease(stream);

        return SUCCEEDED(result) || session->Fail("Could not decode a captured JPEG frame", result);
    }

    bool WriteFrame(EncoderSession* session, const std::vector<uint8_t>& topDownPixels, double presentationSeconds)
    {
        IMFMediaBuffer* mediaBuffer = nullptr;
        IMFSample* sample = nullptr;
        const DWORD stride = static_cast<DWORD>(session->width * 4);
        const DWORD bufferSize = stride * static_cast<DWORD>(session->height);

        HRESULT result = MFCreateMemoryBuffer(bufferSize, &mediaBuffer);
        BYTE* destination = nullptr;
        DWORD maximumLength = 0;
        if (SUCCEEDED(result)) result = mediaBuffer->Lock(&destination, &maximumLength, nullptr);
        if (SUCCEEDED(result))
        {
            // RGB32 in Media Foundation follows the bottom-up DIB convention. WIC emits
            // top-down scanlines, so reverse row order to keep the Unity frame upright.
            for (int y = 0; y < session->height; ++y)
            {
                const uint8_t* sourceRow = topDownPixels.data() + static_cast<size_t>(y) * stride;
                uint8_t* destinationRow = destination + static_cast<size_t>(session->height - 1 - y) * stride;
                std::memcpy(destinationRow, sourceRow, stride);
            }
            mediaBuffer->Unlock();
            destination = nullptr;
            result = mediaBuffer->SetCurrentLength(bufferSize);
        }

        if (SUCCEEDED(result)) result = MFCreateSample(&sample);
        if (SUCCEEDED(result)) result = sample->AddBuffer(mediaBuffer);

        LONGLONG sampleTime = static_cast<LONGLONG>(std::llround(std::max(0.0, presentationSeconds) * 10000000.0));
        if (sampleTime <= session->lastSampleTime)
            sampleTime = session->lastSampleTime + 1;
        if (SUCCEEDED(result)) result = sample->SetSampleTime(sampleTime);
        if (SUCCEEDED(result)) result = sample->SetSampleDuration(session->frameDuration);
        if (SUCCEEDED(result)) result = session->writer->WriteSample(session->streamIndex, sample);
        if (SUCCEEDED(result)) session->lastSampleTime = sampleTime;

        if (destination != nullptr)
            mediaBuffer->Unlock();
        SafeRelease(sample);
        SafeRelease(mediaBuffer);
        return SUCCEEDED(result) || session->Fail("Media Foundation rejected a captured frame", result);
    }

    bool WriteGpuFrame(EncoderSession* session, void* nativeTexture, double presentationSeconds)
    {
        if (session == nullptr || nativeTexture == nullptr || !session->ready || session->finalized)
            return false;

        ID3D11Texture2D* texture = static_cast<ID3D11Texture2D*>(nativeTexture);
        D3D11_TEXTURE2D_DESC description = {};
        texture->GetDesc(&description);
        if (description.Width != static_cast<UINT>(session->width) ||
            description.Height != static_cast<UINT>(session->height) ||
            description.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
        {
            session->lastError = "The D3D11 capture texture must be BGRA8 and match the encoder dimensions.";
            return false;
        }

        IMFMediaBuffer* mediaBuffer = nullptr;
        IMFSample* sample = nullptr;
        HRESULT result = MFCreateDXGISurfaceBuffer(
            __uuidof(ID3D11Texture2D), texture, 0, FALSE, &mediaBuffer);
        if (SUCCEEDED(result)) result = MFCreateSample(&sample);
        if (SUCCEEDED(result)) result = sample->AddBuffer(mediaBuffer);

        LONGLONG sampleTime = static_cast<LONGLONG>(std::llround(std::max(0.0, presentationSeconds) * 10000000.0));
        if (sampleTime <= session->lastSampleTime)
            sampleTime = session->lastSampleTime + 1;
        if (SUCCEEDED(result)) result = sample->SetSampleTime(sampleTime);
        if (SUCCEEDED(result)) result = sample->SetSampleDuration(session->frameDuration);
        if (SUCCEEDED(result)) result = session->writer->WriteSample(session->streamIndex, sample);
        if (SUCCEEDED(result)) session->lastSampleTime = sampleTime;

        SafeRelease(sample);
        SafeRelease(mediaBuffer);
        return SUCCEEDED(result) || session->Fail("Media Foundation rejected a D3D11 texture frame", result);
    }
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_IsAvailable()
{
    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitializeCom = SUCCEEDED(comResult);
    if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE)
        return 0;

    const HRESULT mediaFoundationResult = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (SUCCEEDED(mediaFoundationResult))
        MFShutdown();
    if (uninitializeCom)
        CoUninitialize();
    return SUCCEEDED(mediaFoundationResult) ? 1 : 0;
}

extern "C" __declspec(dllexport) void* __cdecl MacacaBeaconWindowsVideo_Create(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate)
{
    EncoderSession* session = new (std::nothrow) EncoderSession();
    if (session == nullptr)
        return nullptr;

    session->width = std::max(2, width & ~1);
    session->height = std::max(2, height & ~1);
    session->framesPerSecond = std::max(1, framesPerSecond);
    session->frameDuration = 10000000LL / session->framesPerSecond;

    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    session->uninitializeCom = SUCCEEDED(result);
    if (FAILED(result) && result != RPC_E_CHANGED_MODE)
    {
        session->Fail("Could not initialize COM", result);
        return session;
    }

    result = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(result))
    {
        session->Fail("Could not start Media Foundation", result);
        return session;
    }
    session->mediaFoundationStarted = true;

    result = CoCreateInstance(
        CLSID_WICImagingFactory,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&session->imagingFactory));
    if (FAILED(result))
    {
        session->Fail("Could not create Windows Imaging Component", result);
        return session;
    }

    const std::wstring widePath = Utf8ToWide(outputPath);
    if (widePath.empty())
    {
        session->lastError = "The MP4 output path was empty or invalid UTF-8.";
        return session;
    }

    // Sink Writer will not consistently replace an existing destination across Windows
    // versions. The managed caller always supplies a newly staged temporary path.
    DeleteFileW(widePath.c_str());

    IMFAttributes* attributes = nullptr;
    result = MFCreateAttributes(&attributes, 3);
    if (SUCCEEDED(result)) result = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
    if (SUCCEEDED(result)) result = attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
    // Put MP4 metadata before media bytes so Slack and browsers can inspect/preview the
    // attachment without first scanning the complete upload (fast-start layout).
    if (SUCCEEDED(result)) result = attributes->SetUINT32(MacacaMpeg4SinkMoovBeforeMdat, TRUE);
    if (SUCCEEDED(result)) result = MFCreateSinkWriterFromURL(widePath.c_str(), nullptr, attributes, &session->writer);
    SafeRelease(attributes);
    if (FAILED(result))
    {
        session->Fail("Could not create the MP4 sink writer", result);
        return session;
    }

    if (!ConfigureOutput(session, std::max(128000, bitrate)))
        return session;
    if (!ConfigureInput(session))
        return session;

    result = session->writer->BeginWriting();
    if (FAILED(result))
    {
        session->Fail("Could not begin writing MP4 samples", result);
        return session;
    }

    session->ready = true;
    return session;
}

static void MacacaBeaconWindowsVideo_RenderEvent(int eventId, void* data)
{
    if (eventId != 1)
        return;
    GpuSubmit* submit = static_cast<GpuSubmit*>(data);
    EncoderSession* session = submit == nullptr ? nullptr : submit->session;
    if (session != nullptr)
        WriteGpuFrame(session, submit->nativeTexture, submit->presentationSeconds);
    if (session != nullptr)
        InterlockedDecrement(&session->pendingGpuEvents);
    delete submit;
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_GpuIsAvailable()
{
    return 1;
}

extern "C" __declspec(dllexport) void* __cdecl MacacaBeaconWindowsVideo_GpuGetRenderEventFunc()
{
    return reinterpret_cast<void*>(&MacacaBeaconWindowsVideo_RenderEvent);
}

extern "C" __declspec(dllexport) void* __cdecl MacacaBeaconWindowsVideo_GpuAllocateSubmitData(
    void* pointer,
    void* nativeTexture,
    double presentationSeconds)
{
    EncoderSession* session = static_cast<EncoderSession*>(pointer);
    if (session == nullptr || nativeTexture == nullptr || !session->ready || session->finalized)
        return nullptr;
    GpuSubmit* submit = new (std::nothrow) GpuSubmit();
    if (submit == nullptr)
        return nullptr;
    submit->session = session;
    submit->nativeTexture = nativeTexture;
    submit->presentationSeconds = presentationSeconds;
    InterlockedIncrement(&session->pendingGpuEvents);
    return submit;
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_AddJpeg(
    void* pointer,
    const uint8_t* jpegBytes,
    int byteCount,
    double presentationSeconds)
{
    EncoderSession* session = static_cast<EncoderSession*>(pointer);
    if (session == nullptr || !session->ready || session->finalized || jpegBytes == nullptr || byteCount <= 0)
        return 0;

    std::vector<uint8_t> pixels;
    if (!DecodeJpeg(session, jpegBytes, byteCount, pixels))
        return 0;
    return WriteFrame(session, pixels, presentationSeconds) ? 1 : 0;
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_AddRgba(
    void* pointer,
    const uint8_t* rgbaBytes,
    int byteCount,
    int sourceWidth,
    int sourceHeight,
    double presentationSeconds)
{
    EncoderSession* session = static_cast<EncoderSession*>(pointer);
    if (session == nullptr || !session->ready || session->finalized || rgbaBytes == nullptr ||
        sourceWidth <= 0 || sourceHeight <= 0 ||
        byteCount < sourceWidth * sourceHeight * 4)
        return 0;

    std::vector<uint8_t> pixels(static_cast<size_t>(session->width) * session->height * 4);
    for (int y = 0; y < session->height; ++y)
    {
        const int sourceY = std::min(sourceHeight - 1, y * sourceHeight / session->height);
        for (int x = 0; x < session->width; ++x)
        {
            const int sourceX = std::min(sourceWidth - 1, x * sourceWidth / session->width);
            const uint8_t* source = rgbaBytes + (static_cast<size_t>(sourceY) * sourceWidth + sourceX) * 4;
            uint8_t* destination = pixels.data() + (static_cast<size_t>(y) * session->width + x) * 4;
            destination[0] = source[2];
            destination[1] = source[1];
            destination[2] = source[0];
            destination[3] = 255;
        }
    }
    return WriteFrame(session, pixels, presentationSeconds) ? 1 : 0;
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_Finish(void* pointer)
{
    EncoderSession* session = static_cast<EncoderSession*>(pointer);
    if (session == nullptr || !session->ready || session->finalized)
        return 0;
    while (InterlockedCompareExchange(&session->pendingGpuEvents, 0, 0) != 0)
        Sleep(0);
    if (session->lastSampleTime < 0)
    {
        session->lastError = "No captured video frame was written.";
        return 0;
    }

    const HRESULT result = session->writer->Finalize();
    session->finalized = true;
    session->ready = false;
    if (FAILED(result))
    {
        session->Fail("Could not finalize the MP4 file", result);
        return 0;
    }
    return 1;
}

extern "C" __declspec(dllexport) const char* __cdecl MacacaBeaconWindowsVideo_LastError(void* pointer)
{
    EncoderSession* session = static_cast<EncoderSession*>(pointer);
    if (session == nullptr || session->lastError.empty())
        return nullptr;
    return session->lastError.c_str();
}

extern "C" __declspec(dllexport) void __cdecl MacacaBeaconWindowsVideo_Destroy(void* pointer)
{
    delete static_cast<EncoderSession*>(pointer);
}
