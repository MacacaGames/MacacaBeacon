#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
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
    std::string g_availabilityError;

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
        std::string message(operation == nullptr ? "Media Foundation operation failed" : operation);
        message += " (HRESULT 0x";
        char code[16] = {};
        sprintf_s(code, "%08lX", static_cast<unsigned long>(result));
        message += code;
        message += ")";
        return message;
    }

    struct EncoderSession
    {
        IMFSinkWriter* writer = nullptr;
        IWICImagingFactory* imagingFactory = nullptr;
        IMFDXGIDeviceManager* deviceManager = nullptr;
        IMFVideoSampleAllocatorEx* videoSampleAllocator = nullptr;
        ID3D11Device* d3dDevice = nullptr;
        ID3D11VideoDevice* videoDevice = nullptr;
        ID3D11VideoContext* videoContext = nullptr;
        ID3D11VideoProcessorEnumerator* videoEnumerator = nullptr;
        ID3D11VideoProcessor* videoProcessor = nullptr;
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
        bool gpuInput = false;
        volatile LONG pendingGpuEvents = 0;
        std::string lastError;

        ~EncoderSession()
        {
            SafeRelease(writer);
            SafeRelease(imagingFactory);
            SafeRelease(videoSampleAllocator);
            SafeRelease(deviceManager);
            SafeRelease(videoProcessor);
            SafeRelease(videoEnumerator);
            SafeRelease(videoContext);
            SafeRelease(videoDevice);
            SafeRelease(d3dDevice);
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
        IMFAttributes* allocatorAttributes = nullptr;
        HRESULT result = MFCreateMediaType(&inputType);
        if (SUCCEEDED(result)) result = inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(result)) result = inputType->SetGUID(MF_MT_SUBTYPE, session->gpuInput ? MFVideoFormat_NV12 : MFVideoFormat_RGB32);
        if (SUCCEEDED(result)) result = inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(result)) result = inputType->SetUINT32(MF_MT_DEFAULT_STRIDE, static_cast<UINT32>(session->gpuInput ? session->width : session->width * 4));
        if (SUCCEEDED(result)) result = MFSetAttributeSize(inputType, MF_MT_FRAME_SIZE, session->width, session->height);
        if (SUCCEEDED(result)) result = MFSetAttributeRatio(inputType, MF_MT_FRAME_RATE, session->framesPerSecond, 1);
        if (SUCCEEDED(result)) result = MFSetAttributeRatio(inputType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(result)) result = session->writer->SetInputMediaType(session->streamIndex, inputType, nullptr);
        if (SUCCEEDED(result) && session->gpuInput)
            result = MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&session->videoSampleAllocator));
        if (SUCCEEDED(result) && session->gpuInput)
            result = session->videoSampleAllocator->SetDirectXManager(session->deviceManager);
        if (SUCCEEDED(result) && session->gpuInput)
            result = MFCreateAttributes(&allocatorAttributes, 2);
        if (SUCCEEDED(result) && session->gpuInput)
            result = allocatorAttributes->SetUINT32(MF_SA_D3D11_USAGE, D3D11_USAGE_DEFAULT);
        if (SUCCEEDED(result) && session->gpuInput)
            result = allocatorAttributes->SetUINT32(MF_SA_D3D11_BINDFLAGS, D3D11_BIND_RENDER_TARGET);
        if (SUCCEEDED(result) && session->gpuInput)
            result = session->videoSampleAllocator->InitializeSampleAllocatorEx(2, 4, allocatorAttributes, inputType);
        SafeRelease(allocatorAttributes);
        SafeRelease(inputType);
        return SUCCEEDED(result) || session->Fail("Could not configure the video input stream", result);
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
        result = sample->SetSampleTime(sampleTime);
        if (FAILED(result))
        {
            SafeRelease(sample);
            SafeRelease(mediaBuffer);
            return session->Fail("Could not set the captured sample timestamp", result);
        }
        result = sample->SetSampleDuration(session->frameDuration);
        if (FAILED(result))
        {
            SafeRelease(sample);
            SafeRelease(mediaBuffer);
            return session->Fail("Could not set the captured sample duration", result);
        }
        result = session->writer->WriteSample(session->streamIndex, sample);
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
        if (!session->gpuInput || session->d3dDevice == nullptr || session->videoDevice == nullptr ||
            session->videoContext == nullptr || session->videoEnumerator == nullptr ||
            session->videoProcessor == nullptr || session->videoSampleAllocator == nullptr)
            return session->Fail("The D3D11 video encoder session is not ready", E_UNEXPECTED);

        ID3D11Texture2D* texture = static_cast<ID3D11Texture2D*>(nativeTexture);
        D3D11_TEXTURE2D_DESC description = {};
        texture->GetDesc(&description);
        if (description.Width != static_cast<UINT>(session->width) ||
            description.Height != static_cast<UINT>(session->height) ||
            (description.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
             description.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB &&
             description.Format != DXGI_FORMAT_B8G8R8A8_TYPELESS))
        {
            char details[192] = {};
            sprintf_s(details, "D3D11 capture texture mismatch: format=%u, size=%ux%u, expected BGRA8 %dx%d.",
                static_cast<unsigned>(description.Format), description.Width, description.Height,
                session->width, session->height);
            session->lastError = details;
            return false;
        }

        ID3D11Texture2D* concreteTexture = nullptr;
        IMFSample* sample = nullptr;
        IMFMediaBuffer* mediaBuffer = nullptr;
        IMFDXGIBuffer* dxgiBuffer = nullptr;
        ID3D11Texture2D* nv12Texture = nullptr;
        ID3D11VideoProcessorInputView* inputView = nullptr;
        ID3D11VideoProcessorOutputView* outputView = nullptr;
        ID3D11Texture2D* processorInput = texture;
        HRESULT result = S_OK;
        if (description.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS)
        {
            D3D11_TEXTURE2D_DESC concreteDescription = description;
            concreteDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            concreteDescription.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            concreteDescription.CPUAccessFlags = 0;
            concreteDescription.Usage = D3D11_USAGE_DEFAULT;
            concreteDescription.MiscFlags = 0;
            result = session->d3dDevice->CreateTexture2D(&concreteDescription, nullptr, &concreteTexture);
            if (FAILED(result))
                return session->Fail("Could not create a concrete BGRA8 encoder texture", result);

            ID3D11DeviceContext* context = nullptr;
            session->d3dDevice->GetImmediateContext(&context);
            if (context == nullptr)
            {
                SafeRelease(concreteTexture);
                return session->Fail("Could not acquire the D3D11 immediate context", E_POINTER);
            }
            context->CopyResource(concreteTexture, texture);
            context->Flush();
            SafeRelease(context);
            processorInput = concreteTexture;
        }

        if (SUCCEEDED(result)) result = session->videoSampleAllocator->AllocateSample(&sample);
        if (SUCCEEDED(result)) result = sample->GetBufferByIndex(0, &mediaBuffer);
        if (SUCCEEDED(result))
            result = mediaBuffer->SetCurrentLength(
                static_cast<DWORD>(session->width * session->height * 3 / 2));
        if (SUCCEEDED(result)) result = mediaBuffer->QueryInterface(IID_PPV_ARGS(&dxgiBuffer));
        if (SUCCEEDED(result)) result = dxgiBuffer->GetResource(IID_PPV_ARGS(&nv12Texture));

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputViewDescription = {};
        inputViewDescription.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        inputViewDescription.Texture2D.MipSlice = 0;
        inputViewDescription.Texture2D.ArraySlice = 0;
        if (SUCCEEDED(result))
            result = session->videoDevice->CreateVideoProcessorInputView(
                processorInput, session->videoEnumerator, &inputViewDescription, &inputView);

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDescription = {};
        outputViewDescription.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        outputViewDescription.Texture2D.MipSlice = 0;
        if (SUCCEEDED(result))
            result = session->videoDevice->CreateVideoProcessorOutputView(
                nv12Texture, session->videoEnumerator, &outputViewDescription, &outputView);

        RECT frameRect = { 0, 0, session->width, session->height };
        if (SUCCEEDED(result))
        {
            session->videoContext->VideoProcessorSetStreamFrameFormat(
                session->videoProcessor, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
            session->videoContext->VideoProcessorSetStreamSourceRect(session->videoProcessor, 0, TRUE, &frameRect);
            session->videoContext->VideoProcessorSetStreamDestRect(session->videoProcessor, 0, TRUE, &frameRect);
            session->videoContext->VideoProcessorSetOutputTargetRect(session->videoProcessor, TRUE, &frameRect);
            D3D11_VIDEO_PROCESSOR_STREAM stream = {};
            stream.Enable = TRUE;
            stream.pInputSurface = inputView;
            result = session->videoContext->VideoProcessorBlt(session->videoProcessor, outputView, 0, 1, &stream);
        }

        SafeRelease(outputView);
        SafeRelease(inputView);
        SafeRelease(concreteTexture);

        if (FAILED(result))
        {
            SafeRelease(nv12Texture);
            SafeRelease(dxgiBuffer);
            SafeRelease(mediaBuffer);
            SafeRelease(sample);
            return session->Fail("Could not convert the D3D11 capture texture to NV12", result);
        }

        LONGLONG sampleTime = static_cast<LONGLONG>(std::llround(std::max(0.0, presentationSeconds) * 10000000.0));
        if (sampleTime <= session->lastSampleTime)
            sampleTime = session->lastSampleTime + 1;
        if (SUCCEEDED(result)) result = sample->SetSampleTime(sampleTime);
        if (SUCCEEDED(result)) result = sample->SetSampleDuration(session->frameDuration);
        if (SUCCEEDED(result)) result = session->writer->WriteSample(session->streamIndex, sample);
        if (SUCCEEDED(result)) session->lastSampleTime = sampleTime;

        SafeRelease(nv12Texture);
        SafeRelease(dxgiBuffer);
        SafeRelease(mediaBuffer);
        SafeRelease(sample);
        return SUCCEEDED(result) || session->Fail("Media Foundation rejected a D3D11 texture frame", result);
    }
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_IsAvailable()
{
    g_availabilityError.clear();
    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitializeCom = SUCCEEDED(comResult);
    if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE)
    {
        g_availabilityError = HResultMessage("Could not initialize COM", comResult);
        return 0;
    }

    const HRESULT mediaFoundationResult = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (SUCCEEDED(mediaFoundationResult))
        MFShutdown();
    if (uninitializeCom)
        CoUninitialize();
    if (FAILED(mediaFoundationResult))
        g_availabilityError = HResultMessage("Could not start Media Foundation", mediaFoundationResult);
    return SUCCEEDED(mediaFoundationResult) ? 1 : 0;
}

extern "C" __declspec(dllexport) const char* __cdecl MacacaBeaconWindowsVideo_AvailabilityError()
{
    return g_availabilityError.empty() ? nullptr : g_availabilityError.c_str();
}

static EncoderSession* CreateEncoderSession(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate,
    ID3D11Texture2D* gpuTexture)
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
    result = MFCreateAttributes(&attributes, 4);
    if (SUCCEEDED(result)) result = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
    if (SUCCEEDED(result)) result = attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
    if (SUCCEEDED(result) && gpuTexture != nullptr)
    {
        session->gpuInput = true;
        gpuTexture->GetDevice(&session->d3dDevice);
        if (session->d3dDevice == nullptr)
            result = E_POINTER;
        ID3D11DeviceContext* immediateContext = nullptr;
        if (SUCCEEDED(result))
            session->d3dDevice->GetImmediateContext(&immediateContext);
        if (SUCCEEDED(result) && immediateContext == nullptr)
            result = E_POINTER;
        if (SUCCEEDED(result))
            result = session->d3dDevice->QueryInterface(IID_PPV_ARGS(&session->videoDevice));
        if (SUCCEEDED(result))
            result = immediateContext->QueryInterface(IID_PPV_ARGS(&session->videoContext));
        SafeRelease(immediateContext);

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC contentDescription = {};
        contentDescription.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        contentDescription.InputFrameRate.Numerator = static_cast<UINT>(session->framesPerSecond);
        contentDescription.InputFrameRate.Denominator = 1;
        contentDescription.InputWidth = static_cast<UINT>(session->width);
        contentDescription.InputHeight = static_cast<UINT>(session->height);
        contentDescription.OutputFrameRate = contentDescription.InputFrameRate;
        contentDescription.OutputWidth = contentDescription.InputWidth;
        contentDescription.OutputHeight = contentDescription.InputHeight;
        contentDescription.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        if (SUCCEEDED(result))
            result = session->videoDevice->CreateVideoProcessorEnumerator(
                &contentDescription, &session->videoEnumerator);
        if (SUCCEEDED(result))
            result = session->videoDevice->CreateVideoProcessor(
                session->videoEnumerator, 0, &session->videoProcessor);

        UINT resetToken = 0;
        if (SUCCEEDED(result))
            result = MFCreateDXGIDeviceManager(&resetToken, &session->deviceManager);
        if (SUCCEEDED(result))
            result = session->deviceManager->ResetDevice(session->d3dDevice, resetToken);
        if (SUCCEEDED(result))
            result = attributes->SetUnknown(MF_SINK_WRITER_D3D_MANAGER, session->deviceManager);
    }
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

extern "C" __declspec(dllexport) void* __cdecl MacacaBeaconWindowsVideo_Create(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate)
{
    return CreateEncoderSession(outputPath, width, height, framesPerSecond, bitrate, nullptr);
}

extern "C" __declspec(dllexport) void* __cdecl MacacaBeaconWindowsVideo_GpuCreate(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate,
    void* nativeTexture)
{
    return CreateEncoderSession(outputPath, width, height, framesPerSecond, bitrate,
        static_cast<ID3D11Texture2D*>(nativeTexture));
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
        if (session->lastError.empty())
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

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_ConcatSegments(
    const char* outputPath,
    const char** inputPaths,
    int inputCount)
{
    if (outputPath == nullptr || inputPaths == nullptr || inputCount <= 0)
        return 0;

    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitializeCom = SUCCEEDED(comResult);
    if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE)
        return 0;

    HRESULT result = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(result))
    {
        if (uninitializeCom)
            CoUninitialize();
        return 0;
    }

    IMFSinkWriter* writer = nullptr;
    IMFSourceReader* firstReader = nullptr;
    IMFMediaType* compressedType = nullptr;
    DWORD outputStream = 0;
    const std::wstring wideOutput = Utf8ToWide(outputPath);
    const std::wstring firstInput = Utf8ToWide(inputPaths[0]);
    DeleteFileW(wideOutput.c_str());

    result = MFCreateSourceReaderFromURL(firstInput.c_str(), nullptr, &firstReader);
    if (SUCCEEDED(result))
        result = firstReader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &compressedType);
    if (SUCCEEDED(result))
        result = MFCreateSinkWriterFromURL(wideOutput.c_str(), nullptr, nullptr, &writer);
    if (SUCCEEDED(result))
        result = writer->AddStream(compressedType, &outputStream);
    if (SUCCEEDED(result))
        result = writer->SetInputMediaType(outputStream, compressedType, nullptr);
    if (SUCCEEDED(result))
        result = writer->BeginWriting();

    LONGLONG outputTime = 0;
    for (int pathIndex = 0; SUCCEEDED(result) && pathIndex < inputCount; ++pathIndex)
    {
        IMFSourceReader* reader = nullptr;
        if (pathIndex == 0)
        {
            reader = firstReader;
            firstReader = nullptr;
        }
        else
        {
            const std::wstring input = Utf8ToWide(inputPaths[pathIndex]);
            result = MFCreateSourceReaderFromURL(input.c_str(), nullptr, &reader);
        }

        LONGLONG sourceStart = -1;
        LONGLONG segmentEnd = outputTime;
        while (SUCCEEDED(result) && reader != nullptr)
        {
            DWORD flags = 0;
            LONGLONG timestamp = 0;
            IMFSample* sample = nullptr;
            result = reader->ReadSample(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                0,
                nullptr,
                &flags,
                &timestamp,
                &sample);
            if (FAILED(result) || (flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                SafeRelease(sample);
                break;
            }
            if (sample == nullptr)
                continue;

            if (sourceStart < 0)
                sourceStart = timestamp;
            LONGLONG duration = 0;
            if (FAILED(sample->GetSampleDuration(&duration)) || duration <= 0)
                duration = 1;
            const LONGLONG adjustedTime = outputTime + std::max<LONGLONG>(0, timestamp - sourceStart);
            sample->SetSampleTime(adjustedTime);
            result = writer->WriteSample(outputStream, sample);
            segmentEnd = std::max(segmentEnd, adjustedTime + duration);
            SafeRelease(sample);
        }
        outputTime = segmentEnd;
        SafeRelease(reader);
    }

    if (SUCCEEDED(result))
        result = writer->Finalize();
    SafeRelease(compressedType);
    SafeRelease(firstReader);
    SafeRelease(writer);
    MFShutdown();
    if (uninitializeCom)
        CoUninitialize();
    return SUCCEEDED(result) ? 1 : 0;
}
