#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mftransform.h>
#include <shlwapi.h>
#include <wincodec.h>
#include <d3d10_1.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_6.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"

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
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#endif

namespace
{
    std::string g_availabilityError;
    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;
    IUnityGraphicsD3D12v8* g_unityD3D12 = nullptr;
    IUnityGraphicsD3D12* g_unityD3D12Legacy = nullptr;
    int g_renderEventId = 1;

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

    std::string ProbeDx11H264Encoders()
    {
        const MFT_REGISTER_TYPE_INFO input = { MFMediaType_Video, MFVideoFormat_NV12 };
        const MFT_REGISTER_TYPE_INFO output = { MFMediaType_Video, MFVideoFormat_H264 };
        IMFActivate** hardware = nullptr;
        IMFActivate** software = nullptr;
        UINT32 hardwareCount = 0;
        UINT32 softwareCount = 0;
        UINT32 d3d11AwareCount = 0;
        UINT32 inspectionFailures = 0;
        const HRESULT hardwareResult = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
            &input, &output, &hardware, &hardwareCount);
        if (SUCCEEDED(hardwareResult))
        {
            for (UINT32 index = 0; index < hardwareCount; ++index)
            {
                IMFTransform* transform = nullptr;
                IMFAttributes* attributes = nullptr;
                UINT32 d3d11Aware = FALSE;
                HRESULT result = hardware[index]->ActivateObject(IID_PPV_ARGS(&transform));
                if (SUCCEEDED(result))
                    result = transform->GetAttributes(&attributes);
                if (SUCCEEDED(result))
                    result = attributes->GetUINT32(MF_SA_D3D11_AWARE, &d3d11Aware);
                if (SUCCEEDED(result) && d3d11Aware != FALSE)
                    ++d3d11AwareCount;
                else if (FAILED(result))
                    ++inspectionFailures;
                SafeRelease(attributes);
                SafeRelease(transform);
                hardware[index]->ShutdownObject();
                SafeRelease(hardware[index]);
            }
        }
        CoTaskMemFree(hardware);

        const HRESULT softwareResult = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT |
                MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            &input, &output, &software, &softwareCount);
        if (SUCCEEDED(softwareResult))
        {
            for (UINT32 index = 0; index < softwareCount; ++index)
                SafeRelease(software[index]);
        }
        CoTaskMemFree(software);

        char summary[320] = {};
        sprintf_s(summary,
            " DX11 H.264 MFT probe: hardware NV12->H.264=%u (D3D11-aware=%u, inspection failures=%u, enum HRESULT=0x%08lX), software=%u (enum HRESULT=0x%08lX).",
            hardwareCount, d3d11AwareCount, inspectionFailures,
            static_cast<unsigned long>(hardwareResult), softwareCount,
            static_cast<unsigned long>(softwareResult));
        return summary;
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
        bool d3d12Input = false;
        volatile LONG pendingGpuEvents = 0;
        std::string lastError;
        std::string outputPathUtf8;
        std::wstring outputPath;
        int bitrate = 0;
        EncoderSession* d3d12Delegate = nullptr;
        ID3D12Device* d3d12Device = nullptr;
        ID3D12CommandAllocator* d3d12CommandAllocator = nullptr;
        ID3D12GraphicsCommandList* d3d12CommandList = nullptr;
        ID3D12Resource* d3d12SharedTexture = nullptr;
        ID3D11Texture2D* d3d11SharedTexture = nullptr;
        ID3D11Texture2D* d3d11CopyTexture = nullptr;
        ID3D12Fence* sharedFence = nullptr;
        ID3D12Fence* d3d12ReleaseFence = nullptr;
        ID3D11DeviceContext4* d3d11Context4 = nullptr;
        ID3D11Query* d3d11CompletionQuery = nullptr;
        UINT64 sharedFenceValue = 0;
        UINT64 pendingD3D12FenceValue = 0;
        UINT64 d3d12ReleaseFenceValue = 0;
        bool d3d12FramePending = false;
        double pendingPresentationSeconds = 0.0;
        volatile LONG d3d12WorkerBusy = 0;
        volatile LONG d3d12WorkerStop = 0;
        volatile LONG d3d12WorkerInitialized = 0;
        volatile LONG d3d12WorkerFinalizeRequested = 0;
        volatile LONG d3d12WorkerFinalizeResult = 0;
        HANDLE d3d12WorkerThread = nullptr;
        HANDLE d3d12WorkerWakeEvent = nullptr;
        HANDLE d3d12WorkerIdleEvent = nullptr;
        HANDLE d3d12WorkerReadyEvent = nullptr;
        HANDLE sharedFenceEvent = nullptr;
        HANDLE sharedTextureHandle = nullptr;

        ~EncoderSession()
        {
            if (d3d12WorkerThread != nullptr)
            {
                InterlockedExchange(&d3d12WorkerStop, 1);
                if (d3d12WorkerWakeEvent != nullptr)
                    SetEvent(d3d12WorkerWakeEvent);
                WaitForSingleObject(d3d12WorkerThread, 10000);
                CloseHandle(d3d12WorkerThread);
            }
            if (d3d12WorkerWakeEvent != nullptr)
                CloseHandle(d3d12WorkerWakeEvent);
            if (d3d12WorkerIdleEvent != nullptr)
                CloseHandle(d3d12WorkerIdleEvent);
            if (d3d12WorkerReadyEvent != nullptr)
                CloseHandle(d3d12WorkerReadyEvent);
            SafeRelease(writer);
            SafeRelease(imagingFactory);
            SafeRelease(videoSampleAllocator);
            SafeRelease(deviceManager);
            SafeRelease(videoProcessor);
            SafeRelease(videoEnumerator);
            SafeRelease(videoContext);
            SafeRelease(videoDevice);
            SafeRelease(d3dDevice);
            // D3D11, Media Foundation and their delegate are created and
            // released by the encoder worker that uses them.
            SafeRelease(d3d12ReleaseFence);
            SafeRelease(sharedFence);
            SafeRelease(d3d11SharedTexture);
            SafeRelease(d3d11CopyTexture);
            SafeRelease(d3d12SharedTexture);
            SafeRelease(d3d12CommandList);
            SafeRelease(d3d12CommandAllocator);
            SafeRelease(d3d12Device);
            if (sharedFenceEvent != nullptr)
                CloseHandle(sharedFenceEvent);
            if (sharedTextureHandle != nullptr)
                CloseHandle(sharedTextureHandle);
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
        const char* failedOperation = "MFCreateMediaType for H.264 output";
        HRESULT result = MFCreateMediaType(&outputType);
        if (SUCCEEDED(result)) { failedOperation = "Set H.264 output major type"; result = outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video); }
        if (SUCCEEDED(result)) { failedOperation = "Set H.264 output subtype"; result = outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264); }
        if (SUCCEEDED(result)) { failedOperation = "Set H.264 output bitrate"; result = outputType->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(bitrate)); }
        if (SUCCEEDED(result)) { failedOperation = "Set H.264 output interlace mode"; result = outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive); }
        if (SUCCEEDED(result)) { failedOperation = "Set H.264 output frame size"; result = MFSetAttributeSize(outputType, MF_MT_FRAME_SIZE, session->width, session->height); }
        if (SUCCEEDED(result)) { failedOperation = "Set H.264 output frame rate"; result = MFSetAttributeRatio(outputType, MF_MT_FRAME_RATE, session->framesPerSecond, 1); }
        if (SUCCEEDED(result)) { failedOperation = "Set H.264 output pixel aspect ratio"; result = MFSetAttributeRatio(outputType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1); }
        if (SUCCEEDED(result)) { failedOperation = "Add H.264 output stream"; result = session->writer->AddStream(outputType, &session->streamIndex); }
        SafeRelease(outputType);
        return SUCCEEDED(result) || session->Fail(failedOperation, result);
    }

    bool ConfigureInput(EncoderSession* session, bool probeDx11EncoderOnNegotiationFailure)
    {
        IMFMediaType* inputType = nullptr;
        IMFAttributes* allocatorAttributes = nullptr;
        const char* failedOperation = "MFCreateMediaType for video input";
        HRESULT result = MFCreateMediaType(&inputType);
        if (SUCCEEDED(result)) { failedOperation = "Set video input major type"; result = inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video); }
        if (SUCCEEDED(result)) { failedOperation = session->gpuInput ? "Set NV12 GPU input subtype" : "Set RGB32 CPU input subtype"; result = inputType->SetGUID(MF_MT_SUBTYPE, session->gpuInput ? MFVideoFormat_NV12 : MFVideoFormat_RGB32); }
        if (SUCCEEDED(result)) { failedOperation = "Set video input interlace mode"; result = inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive); }
        if (SUCCEEDED(result)) { failedOperation = "Set video input stride"; result = inputType->SetUINT32(MF_MT_DEFAULT_STRIDE, static_cast<UINT32>(session->gpuInput ? session->width : session->width * 4)); }
        if (SUCCEEDED(result)) { failedOperation = "Set video input frame size"; result = MFSetAttributeSize(inputType, MF_MT_FRAME_SIZE, session->width, session->height); }
        if (SUCCEEDED(result)) { failedOperation = "Set video input frame rate"; result = MFSetAttributeRatio(inputType, MF_MT_FRAME_RATE, session->framesPerSecond, 1); }
        if (SUCCEEDED(result)) { failedOperation = "Set video input pixel aspect ratio"; result = MFSetAttributeRatio(inputType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1); }
        if (SUCCEEDED(result)) { failedOperation = "Set Sink Writer video input media type"; result = session->writer->SetInputMediaType(session->streamIndex, inputType, nullptr); }
        if (SUCCEEDED(result) && session->gpuInput)
        {
            failedOperation = "MFCreateVideoSampleAllocatorEx for DXGI video samples";
            result = MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&session->videoSampleAllocator));
        }
        if (SUCCEEDED(result) && session->gpuInput)
        {
            failedOperation = "SetDirectXManager on the DXGI video sample allocator";
            result = session->videoSampleAllocator->SetDirectXManager(session->deviceManager);
        }
        if (SUCCEEDED(result) && session->gpuInput)
        {
            failedOperation = "MFCreateAttributes for the DXGI video sample allocator";
            result = MFCreateAttributes(&allocatorAttributes, 2);
        }
        if (SUCCEEDED(result) && session->gpuInput)
        {
            failedOperation = "Set D3D11 usage for the DXGI video sample allocator";
            result = allocatorAttributes->SetUINT32(MF_SA_D3D11_USAGE, D3D11_USAGE_DEFAULT);
        }
        if (SUCCEEDED(result) && session->gpuInput)
        {
            failedOperation = "Set D3D11 bind flags for the DXGI video sample allocator";
            result = allocatorAttributes->SetUINT32(MF_SA_D3D11_BINDFLAGS, D3D11_BIND_RENDER_TARGET);
        }
        if (SUCCEEDED(result) && session->gpuInput)
        {
            failedOperation = "InitializeSampleAllocatorEx for DXGI video samples";
            result = session->videoSampleAllocator->InitializeSampleAllocatorEx(2, 4, allocatorAttributes, inputType);
        }
        SafeRelease(allocatorAttributes);
        SafeRelease(inputType);
        if (SUCCEEDED(result))
            return true;
        session->Fail(failedOperation, result);
        if (probeDx11EncoderOnNegotiationFailure && result == E_NOTIMPL)
            session->lastError += ProbeDx11H264Encoders();
        return false;
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

    void ConfigureD3D12PluginEvent()
    {
        if (g_unityD3D12 == nullptr || g_renderEventId <= 0)
            return;
        UnityD3D12PluginEventConfig config = {};
        config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_Allow;
        config.flags = kUnityD3D12EventConfigFlag_FlushCommandBuffers |
            kUnityD3D12EventConfigFlag_SyncWorkerThreads;
        config.ensureActiveRenderTextureIsBound = false;
        g_unityD3D12->ConfigureEvent(g_renderEventId, &config);
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        if (g_unityGraphics == nullptr || g_unityInterfaces == nullptr)
            return;
        if (eventType == kUnityGfxDeviceEventInitialize &&
            g_unityGraphics->GetRenderer() == kUnityGfxRendererD3D12)
        {
            g_unityD3D12 = g_unityInterfaces->Get<IUnityGraphicsD3D12v8>();
            g_unityD3D12Legacy = g_unityInterfaces->Get<IUnityGraphicsD3D12>();
            ConfigureD3D12PluginEvent();
        }
        else if (eventType == kUnityGfxDeviceEventShutdown)
        {
            g_unityD3D12 = nullptr;
            g_unityD3D12Legacy = nullptr;
        }
    }
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_unityInterfaces = unityInterfaces;
    g_unityGraphics = unityInterfaces == nullptr ? nullptr : unityInterfaces->Get<IUnityGraphics>();
    if (g_unityGraphics != nullptr)
    {
        g_renderEventId = g_unityGraphics->ReserveEventIDRange(1);
        if (g_renderEventId <= 0)
            g_renderEventId = 1;
        g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
        OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
    }
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_unityGraphics != nullptr)
        g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    g_unityD3D12 = nullptr;
    g_unityD3D12Legacy = nullptr;
    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;
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
    ID3D11Texture2D* gpuTexture,
    bool probeDx11EncoderOnNegotiationFailure)
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
    const char* failedOperation = "MFCreateAttributes for the MP4 sink writer";
    result = MFCreateAttributes(&attributes, 4);
    if (SUCCEEDED(result)) { failedOperation = "Enable Media Foundation hardware transforms"; result = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE); }
    if (SUCCEEDED(result)) { failedOperation = "Disable Media Foundation Sink Writer throttling"; result = attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE); }
    if (SUCCEEDED(result) && gpuTexture != nullptr)
    {
        session->gpuInput = true;
        gpuTexture->GetDevice(&session->d3dDevice);
        if (session->d3dDevice == nullptr)
        {
            failedOperation = "Get D3D11 device from the Unity capture texture";
            result = E_POINTER;
        }
        ID3D11DeviceContext* immediateContext = nullptr;
        if (SUCCEEDED(result))
            session->d3dDevice->GetImmediateContext(&immediateContext);
        if (SUCCEEDED(result) && immediateContext == nullptr)
        {
            failedOperation = "Get D3D11 immediate context";
            result = E_POINTER;
        }
        if (SUCCEEDED(result))
        {
            failedOperation = "Query ID3D11VideoDevice";
            result = session->d3dDevice->QueryInterface(IID_PPV_ARGS(&session->videoDevice));
        }
        if (SUCCEEDED(result))
        {
            failedOperation = "Query ID3D11VideoContext";
            result = immediateContext->QueryInterface(IID_PPV_ARGS(&session->videoContext));
        }
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
        {
            failedOperation = "CreateVideoProcessorEnumerator";
            result = session->videoDevice->CreateVideoProcessorEnumerator(
                &contentDescription, &session->videoEnumerator);
        }
        if (SUCCEEDED(result))
        {
            failedOperation = "CreateVideoProcessor";
            result = session->videoDevice->CreateVideoProcessor(
                session->videoEnumerator, 0, &session->videoProcessor);
        }

        UINT resetToken = 0;
        if (SUCCEEDED(result))
        {
            failedOperation = "MFCreateDXGIDeviceManager";
            result = MFCreateDXGIDeviceManager(&resetToken, &session->deviceManager);
        }
        if (SUCCEEDED(result))
        {
            failedOperation = "ResetDevice on IMFDXGIDeviceManager";
            result = session->deviceManager->ResetDevice(session->d3dDevice, resetToken);
        }
        if (SUCCEEDED(result))
        {
            failedOperation = "Set MF_SINK_WRITER_D3D_MANAGER";
            result = attributes->SetUnknown(MF_SINK_WRITER_D3D_MANAGER, session->deviceManager);
        }
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "MFCreateSinkWriterFromURL for MP4 output";
        result = MFCreateSinkWriterFromURL(widePath.c_str(), nullptr, attributes, &session->writer);
    }
    SafeRelease(attributes);
    if (FAILED(result))
    {
        session->Fail(failedOperation, result);
        return session;
    }

    if (!ConfigureOutput(session, std::max(128000, bitrate)))
        return session;
    if (!ConfigureInput(session, probeDx11EncoderOnNegotiationFailure))
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

static EncoderSession* CreateD3D12PendingSession(
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
    session->d3d12Input = true;
    session->outputPathUtf8 = outputPath == nullptr ? std::string() : outputPath;
    session->outputPath = Utf8ToWide(outputPath);
    session->bitrate = std::max(128000, bitrate);
    if (session->outputPath.empty())
        session->lastError = "The MP4 output path was empty or invalid UTF-8.";
    return session;
}

static bool WaitForSharedFence(EncoderSession* session, UINT64 value)
{
    if (session == nullptr || session->sharedFence == nullptr || value == 0)
        return true;
    if (session->sharedFence->GetCompletedValue() >= value)
        return true;
    if (session->sharedFenceEvent == nullptr)
        session->sharedFenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (session->sharedFenceEvent == nullptr)
        return false;
    HRESULT result = session->sharedFence->SetEventOnCompletion(value, session->sharedFenceEvent);
    if (FAILED(result))
        return session->Fail("Could not wait for the D3D12 shared fence", result);
    const DWORD waitResult = WaitForSingleObject(session->sharedFenceEvent, 10000);
    if (waitResult == WAIT_OBJECT_0)
        return true;
    const HRESULT deviceResult = session->d3d12Device == nullptr
        ? E_FAIL
        : session->d3d12Device->GetDeviceRemovedReason();
    return session->Fail("Timed out waiting for D3D11/D3D12 shared-fence ownership", FAILED(deviceResult) ? deviceResult : E_FAIL);
}

static bool WaitForD3D11Completion(EncoderSession* session)
{
    if (session == nullptr || session->d3d11Context4 == nullptr ||
        session->d3d11CompletionQuery == nullptr)
        return false;
    const ULONGLONG deadline = GetTickCount64() + 10000;
    while (true)
    {
        const HRESULT result = session->d3d11Context4->GetData(
            session->d3d11CompletionQuery, nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (result == S_OK)
            return true;
        if (FAILED(result))
            return session->Fail("Could not wait for the D3D11 GPU capture", result);
        if (GetTickCount64() >= deadline)
        {
            const HRESULT deviceResult = session->d3d12Device == nullptr
                ? E_FAIL
                : session->d3d12Device->GetDeviceRemovedReason();
            return session->Fail("Timed out waiting for the D3D11 GPU capture", FAILED(deviceResult) ? deviceResult : E_FAIL);
        }
        Sleep(1);
    }
}

static bool InitializeD3D11EncoderWorker(EncoderSession* session)
{
    if (session == nullptr || session->d3d12Device == nullptr)
        return false;

    IDXGIFactory6* factory = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    ID3D11Device* interopDevice = nullptr;
    ID3D11DeviceContext* interopContext = nullptr;
    ID3D11Device5* interopDevice5 = nullptr;
    ID3D10Multithread* multithread = nullptr;
    const char* failedOperation = "CreateDXGIFactory1";
    HRESULT result = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result))
    {
        failedOperation = "EnumAdapterByLuid";
        result = factory->EnumAdapterByLuid(
            session->d3d12Device->GetAdapterLuid(), IID_PPV_ARGS(&adapter));
    }
    const D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0
    };
    if (SUCCEEDED(result))
    {
        failedOperation = "D3D11CreateDevice";
        result = D3D11CreateDevice(
            adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            featureLevels, ARRAYSIZE(featureLevels), D3D11_SDK_VERSION,
            &interopDevice, nullptr, &interopContext);
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "QueryInterface ID3D10Multithread";
        result = interopContext->QueryInterface(IID_PPV_ARGS(&multithread));
    }
    if (SUCCEEDED(result))
        multithread->SetMultithreadProtected(TRUE);
    if (SUCCEEDED(result))
    {
        failedOperation = "QueryInterface ID3D11Device5";
        result = interopDevice->QueryInterface(IID_PPV_ARGS(&interopDevice5));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "QueryInterface ID3D11DeviceContext4";
        result = interopContext->QueryInterface(IID_PPV_ARGS(&session->d3d11Context4));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "OpenSharedResource1 texture";
        result = interopDevice5->OpenSharedResource1(
            session->sharedTextureHandle, IID_PPV_ARGS(&session->d3d11SharedTexture));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "Create D3D11 video processor texture";
        D3D11_TEXTURE2D_DESC copyDescription = {};
        copyDescription.Width = static_cast<UINT>(session->width);
        copyDescription.Height = static_cast<UINT>(session->height);
        copyDescription.MipLevels = 1;
        copyDescription.ArraySize = 1;
        copyDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        copyDescription.SampleDesc.Count = 1;
        copyDescription.Usage = D3D11_USAGE_DEFAULT;
        copyDescription.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        result = interopDevice->CreateTexture2D(
            &copyDescription, nullptr, &session->d3d11CopyTexture);
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "Create D3D11 completion query";
        D3D11_QUERY_DESC queryDescription = {};
        queryDescription.Query = D3D11_QUERY_EVENT;
        result = interopDevice->CreateQuery(
            &queryDescription, &session->d3d11CompletionQuery);
    }
    SafeRelease(multithread);
    SafeRelease(interopDevice5);
    SafeRelease(interopContext);
    SafeRelease(interopDevice);
    SafeRelease(adapter);
    SafeRelease(factory);
    if (FAILED(result))
        return session->Fail(failedOperation, result);

    session->d3d12Delegate = CreateEncoderSession(
        session->outputPathUtf8.c_str(), session->width, session->height,
        session->framesPerSecond, session->bitrate, session->d3d11CopyTexture, false);
    if (session->d3d12Delegate == nullptr || !session->d3d12Delegate->ready)
    {
        if (session->d3d12Delegate != nullptr && !session->d3d12Delegate->lastError.empty())
            session->lastError = session->d3d12Delegate->lastError;
        else
            session->lastError = "Could not initialize the D3D12 Media Foundation delegate.";
        return false;
    }
    return true;
}

static DWORD WINAPI D3D12EncoderWorker(void* context);

static bool InitializeD3D12Interop(EncoderSession* session, ID3D12Resource* source)
{
    if (session == nullptr || source == nullptr || g_unityD3D12 == nullptr)
        return session != nullptr && session->Fail("Unity D3D12 interfaces are unavailable", E_NOINTERFACE);

    D3D12_RESOURCE_DESC sourceDescription = source->GetDesc();
    if (sourceDescription.Width != static_cast<UINT64>(session->width) ||
        sourceDescription.Height != static_cast<UINT>(session->height) ||
        (sourceDescription.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
         sourceDescription.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB &&
         sourceDescription.Format != DXGI_FORMAT_B8G8R8A8_TYPELESS))
        return session->Fail("The D3D12 capture texture must be BGRA8 and match the encoder dimensions", E_INVALIDARG);

    ID3D12Device* device = g_unityD3D12->GetDevice();
    if (device == nullptr)
        return session->Fail("Unity did not provide a D3D12 device", E_POINTER);
    device->AddRef();
    session->d3d12Device = device;

    const char* failedOperation = "CreateCommittedResource shared texture";
    HRESULT result = S_OK;

    D3D12_HEAP_PROPERTIES heapProperties = {};
    heapProperties.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC sharedDescription = {};
    sharedDescription.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    sharedDescription.Width = static_cast<UINT64>(session->width);
    sharedDescription.Height = static_cast<UINT>(session->height);
    sharedDescription.DepthOrArraySize = 1;
    sharedDescription.MipLevels = 1;
    sharedDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sharedDescription.SampleDesc.Count = 1;
    sharedDescription.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    // D3D11 maps ALLOW_RENDER_TARGET to D3D11_BIND_RENDER_TARGET and, because
    // shader resources are not denied, exposes the SRV bind required by the
    // video processor when it opens this D3D12 shared texture.
    sharedDescription.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET |
        D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
    if (SUCCEEDED(result))
    {
        failedOperation = "CreateCommittedResource shared texture";
        result = device->CreateCommittedResource(
            &heapProperties, D3D12_HEAP_FLAG_SHARED, &sharedDescription,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&session->d3d12SharedTexture));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "CreateSharedHandle texture";
        result = device->CreateSharedHandle(
            session->d3d12SharedTexture, nullptr, GENERIC_ALL, nullptr, &session->sharedTextureHandle);
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "CreateFence capture fence";
        result = device->CreateFence(
            0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&session->sharedFence));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "CreateFence release fence";
        result = device->CreateFence(
            0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&session->d3d12ReleaseFence));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "CreateCommandAllocator";
        result = device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&session->d3d12CommandAllocator));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "CreateCommandList";
        result = device->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_DIRECT, session->d3d12CommandAllocator,
            nullptr, IID_PPV_ARGS(&session->d3d12CommandList));
    }
    if (SUCCEEDED(result))
    {
        failedOperation = "CloseCommandList";
        result = session->d3d12CommandList->Close();
    }
    if (FAILED(result))
        return session->Fail(failedOperation, result);


    session->d3d12WorkerWakeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    session->d3d12WorkerIdleEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    session->d3d12WorkerReadyEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (session->d3d12WorkerWakeEvent == nullptr || session->d3d12WorkerIdleEvent == nullptr ||
        session->d3d12WorkerReadyEvent == nullptr)
        return session->Fail("Could not create the D3D12 encoder worker events", HRESULT_FROM_WIN32(GetLastError()));
    session->d3d12WorkerThread = CreateThread(
        nullptr, 0, &D3D12EncoderWorker, session, 0, nullptr);
    if (session->d3d12WorkerThread == nullptr)
        return session->Fail("Could not create the D3D12 encoder worker", HRESULT_FROM_WIN32(GetLastError()));
    if (WaitForSingleObject(session->d3d12WorkerReadyEvent, 10000) != WAIT_OBJECT_0)
        return session->Fail("Timed out initializing the D3D12 encoder worker", E_FAIL);
    if (InterlockedCompareExchange(&session->d3d12WorkerInitialized, 0, 0) <= 0)
        return false;
    return true;
}

static bool ConsumePendingD3D12Frame(EncoderSession* session)
{
    if (session == nullptr || !session->d3d12FramePending)
        return true;

    // Do not enter the NVIDIA D3D11 driver while Unity's submission callback
    // is still unwinding. Completion here proves the direct-queue copy and
    // signal have executed, not merely that Signal() accepted the command.
    if (!WaitForSharedFence(session, session->pendingD3D12FenceValue))
        return false;

    session->d3d11Context4->CopyResource(
        session->d3d11CopyTexture, session->d3d11SharedTexture);
    session->d3d11Context4->End(session->d3d11CompletionQuery);
    HRESULT result = S_OK;
    const UINT64 releaseFenceValue = session->d3d12ReleaseFenceValue + 1;
    session->d3d11Context4->Flush();
    if (!WaitForD3D11Completion(session))
        return false;

    // Only the shared-to-local copy is flushed here, before the local NV12
    // sample is handed to Media Foundation. This avoids re-entering the same
    // immediate context after SinkWriter has accepted the sample.
    result = session->d3d12ReleaseFence->Signal(releaseFenceValue);
    if (FAILED(result))
        return session->Fail("Could not release the capture texture to D3D12", result);

    const bool written = WriteGpuFrame(
        session->d3d12Delegate, session->d3d11CopyTexture,
        session->pendingPresentationSeconds);
    session->d3d11Context4->Flush();
    if (!written)
        return false;

    session->lastSampleTime = session->d3d12Delegate->lastSampleTime;
    session->d3d12ReleaseFenceValue = releaseFenceValue;
    session->d3d12FramePending = false;
    return true;
}

static DWORD WINAPI D3D12EncoderWorker(void* context)
{
    auto* session = static_cast<EncoderSession*>(context);
    if (session == nullptr)
        return 1;
    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitializeCom = SUCCEEDED(comResult);
    if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE)
        session->Fail("Could not initialize the D3D12 encoder worker COM apartment", comResult);

    const bool initialized =
        (SUCCEEDED(comResult) || comResult == RPC_E_CHANGED_MODE) &&
        InitializeD3D11EncoderWorker(session);
    InterlockedExchange(&session->d3d12WorkerInitialized, initialized ? 1 : -1);
    SetEvent(session->d3d12WorkerReadyEvent);
    SetEvent(session->d3d12WorkerIdleEvent);

    while (initialized &&
        InterlockedCompareExchange(&session->d3d12WorkerStop, 0, 0) == 0)
    {
        const DWORD waitResult = WaitForSingleObject(session->d3d12WorkerWakeEvent, INFINITE);
        if (waitResult != WAIT_OBJECT_0 ||
            InterlockedCompareExchange(&session->d3d12WorkerStop, 0, 0) != 0)
            break;
        if (InterlockedExchange(&session->d3d12WorkerFinalizeRequested, 0) != 0)
        {
            bool finalized = false;
            EncoderSession* delegate = session->d3d12Delegate;
            if (delegate != nullptr && delegate->ready && !delegate->finalized &&
                delegate->lastSampleTime >= 0)
            {
                const HRESULT result = delegate->writer->Finalize();
                delegate->finalized = true;
                delegate->ready = false;
                finalized = SUCCEEDED(result);
                if (!finalized)
                    delegate->Fail("Could not finalize the MP4 file", result);
            }
            else if (delegate != nullptr && delegate->lastSampleTime < 0)
            {
                delegate->lastError = "No captured video frame was written.";
            }
            InterlockedExchange(
                &session->d3d12WorkerFinalizeResult, finalized ? 1 : -1);
        }
        else
        {
            ConsumePendingD3D12Frame(session);
        }
        InterlockedExchange(&session->d3d12WorkerBusy, 0);
        SetEvent(session->d3d12WorkerIdleEvent);
    }

    delete session->d3d12Delegate;
    session->d3d12Delegate = nullptr;
    SafeRelease(session->d3d11CompletionQuery);
    SafeRelease(session->d3d11Context4);
    SafeRelease(session->d3d11SharedTexture);
    SafeRelease(session->d3d11CopyTexture);

    if (uninitializeCom)
        CoUninitialize();
    return 0;
}

static bool WriteD3D12Frame(EncoderSession* session, void* nativeTexture, double presentationSeconds)
{
    if (session == nullptr || nativeTexture == nullptr || session->finalized)
        return false;

    auto* source = static_cast<ID3D12Resource*>(nativeTexture);
    if (session->d3d12Delegate == nullptr && !InitializeD3D12Interop(session, source))
        return false;

    // Never block Unity's submission thread on the separate D3D11 device. If
    // the worker is still consuming the previous shared texture, this capture
    // frame is dropped and the game render queue continues uninterrupted.
    if (InterlockedCompareExchange(&session->d3d12WorkerBusy, 0, 0) != 0)
        return true;
    if (g_unityD3D12 == nullptr || g_unityD3D12->GetCommandQueue == nullptr)
        return session->Fail("Unity did not provide D3D12 graphics-queue access", E_NOINTERFACE);
    ID3D12CommandQueue* graphicsQueue = g_unityD3D12->GetCommandQueue();
    if (graphicsQueue == nullptr)
        return session->Fail("Unity did not provide a D3D12 graphics queue", E_POINTER);

    D3D12_RESOURCE_STATES sourceState = D3D12_RESOURCE_STATE_COMMON;
    if (g_unityD3D12Legacy != nullptr && g_unityD3D12Legacy->GetResourceState != nullptr)
        g_unityD3D12Legacy->GetResourceState(source, &sourceState);

    HRESULT result = session->d3d12CommandAllocator->Reset();
    if (SUCCEEDED(result))
        result = session->d3d12CommandList->Reset(session->d3d12CommandAllocator, nullptr);
    if (SUCCEEDED(result) && sourceState != D3D12_RESOURCE_STATE_COPY_SOURCE)
    {
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = source;
        barrier.Transition.StateBefore = sourceState;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        session->d3d12CommandList->ResourceBarrier(1, &barrier);
    }
    if (SUCCEEDED(result))
    {
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = session->d3d12SharedTexture;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        session->d3d12CommandList->ResourceBarrier(1, &barrier);
        session->d3d12CommandList->CopyResource(session->d3d12SharedTexture, source);

        D3D12_RESOURCE_BARRIER barriers[2] = {};
        barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[0].Transition.pResource = session->d3d12SharedTexture;
        barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
        barriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[1].Transition.pResource = source;
        barriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
        barriers[1].Transition.StateAfter = sourceState;
        barriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        session->d3d12CommandList->ResourceBarrier(
            sourceState == D3D12_RESOURCE_STATE_COPY_SOURCE ? 1 : 2, barriers);
        result = session->d3d12CommandList->Close();
    }
    if (FAILED(result))
        return session->Fail("Could not record the D3D12 capture copy", result);

    // The event is configured with graphicsQueueAccess=Allow, FlushCommandBuffers
    // and SyncWorkerThreads, so this callback runs on Unity's submission thread
    // after earlier rendering work. Queueing directly here avoids a circular
    // dependency on Unity's end-of-frame fence.
    if (session->d3d12ReleaseFenceValue != 0)
    {
        result = graphicsQueue->Wait(
            session->d3d12ReleaseFence, session->d3d12ReleaseFenceValue);
        if (FAILED(result))
            return session->Fail("Could not reacquire the capture texture on D3D12", result);
    }
    ID3D12CommandList* commandLists[] = { session->d3d12CommandList };
    graphicsQueue->ExecuteCommandLists(ARRAYSIZE(commandLists), commandLists);
    const UINT64 d3d12CopyFenceValue = ++session->sharedFenceValue;
    result = graphicsQueue->Signal(session->sharedFence, d3d12CopyFenceValue);
    if (FAILED(result))
        return session->Fail("Could not synchronize the D3D12 capture copy", result);
    if (g_unityD3D12Legacy != nullptr && g_unityD3D12Legacy->SetResourceState != nullptr)
        g_unityD3D12Legacy->SetResourceState(source, sourceState);

    session->pendingPresentationSeconds = presentationSeconds;
    session->pendingD3D12FenceValue = d3d12CopyFenceValue;
    session->d3d12FramePending = true;
    ResetEvent(session->d3d12WorkerIdleEvent);
    InterlockedExchange(&session->d3d12WorkerBusy, 1);
    if (!SetEvent(session->d3d12WorkerWakeEvent))
    {
        session->d3d12FramePending = false;
        InterlockedExchange(&session->d3d12WorkerBusy, 0);
        SetEvent(session->d3d12WorkerIdleEvent);
        return session->Fail("Could not wake the D3D12 encoder worker", HRESULT_FROM_WIN32(GetLastError()));
    }
    return true;
}

extern "C" __declspec(dllexport) void* __cdecl MacacaBeaconWindowsVideo_Create(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate)
{
    return CreateEncoderSession(outputPath, width, height, framesPerSecond, bitrate, nullptr, false);
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
        static_cast<ID3D11Texture2D*>(nativeTexture), true);
}

extern "C" __declspec(dllexport) void* __cdecl MacacaBeaconWindowsVideo_GpuCreateD3D12(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate)
{
    return CreateD3D12PendingSession(outputPath, width, height, framesPerSecond, bitrate);
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_GpuGetRenderEventId()
{
    return g_renderEventId;
}

static void UNITY_INTERFACE_API MacacaBeaconWindowsVideo_RenderEvent(int eventId, void* data)
{
    if (eventId != g_renderEventId)
        return;
    GpuSubmit* submit = static_cast<GpuSubmit*>(data);
    EncoderSession* session = submit == nullptr ? nullptr : submit->session;
    if (session != nullptr)
    {
        if (session->d3d12Input)
            WriteD3D12Frame(session, submit->nativeTexture, submit->presentationSeconds);
        else
            WriteGpuFrame(session, submit->nativeTexture, submit->presentationSeconds);
    }
    if (session != nullptr)
        InterlockedDecrement(&session->pendingGpuEvents);
    delete submit;
}

extern "C" __declspec(dllexport) int __cdecl MacacaBeaconWindowsVideo_GpuIsAvailable()
{
    if (g_unityGraphics == nullptr)
        return 1;
    if (g_unityGraphics->GetRenderer() == kUnityGfxRendererD3D11)
        return 1;
    return g_unityGraphics->GetRenderer() == kUnityGfxRendererD3D12 && g_unityD3D12 != nullptr ? 1 : 0;
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
    if (session == nullptr || nativeTexture == nullptr ||
        ((!session->d3d12Input && !session->ready) ||
         (session->d3d12Input && session->outputPath.empty())) || session->finalized)
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
    EncoderSession* request = static_cast<EncoderSession*>(pointer);
    if (request == nullptr || request->finalized)
        return 0;
    while (InterlockedCompareExchange(&request->pendingGpuEvents, 0, 0) != 0)
        Sleep(0);
    EncoderSession* session = request;
    if (request->d3d12Input)
    {
        if (request->d3d12WorkerIdleEvent == nullptr ||
            WaitForSingleObject(request->d3d12WorkerIdleEvent, 30000) != WAIT_OBJECT_0)
        {
            request->lastError = "Timed out waiting for the D3D12 encoder worker.";
            return 0;
        }
        session = request->d3d12Delegate;
        if (session == nullptr)
        {
            if (request->lastError.empty())
                request->lastError = "The D3D12 video session did not receive a frame.";
            return 0;
        }
        if (!session->ready || session->finalized || session->lastSampleTime < 0)
        {
            if (session->lastError.empty())
                session->lastError = "No captured video frame was written.";
            return 0;
        }

        ResetEvent(request->d3d12WorkerIdleEvent);
        InterlockedExchange(&request->d3d12WorkerFinalizeResult, 0);
        InterlockedExchange(&request->d3d12WorkerFinalizeRequested, 1);
        InterlockedExchange(&request->d3d12WorkerBusy, 1);
        if (!SetEvent(request->d3d12WorkerWakeEvent) ||
            WaitForSingleObject(request->d3d12WorkerIdleEvent, 30000) != WAIT_OBJECT_0)
        {
            request->lastError = "Timed out finalizing the D3D12 encoder worker.";
            return 0;
        }
        if (InterlockedCompareExchange(&request->d3d12WorkerFinalizeResult, 0, 0) != 1)
            return 0;
        request->lastSampleTime = session->lastSampleTime;
        request->finalized = true;
        request->ready = false;
        return 1;
    }
    if (!session->ready || session->finalized)
    {
        if (request->lastError.empty())
            request->lastError = session->lastError;
        return 0;
    }
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
    if (request != session)
    {
        request->finalized = true;
        request->ready = false;
    }
    return 1;
}

extern "C" __declspec(dllexport) const char* __cdecl MacacaBeaconWindowsVideo_LastError(void* pointer)
{
    EncoderSession* session = static_cast<EncoderSession*>(pointer);
    if (session != nullptr && session->d3d12Input && session->d3d12Delegate != nullptr &&
        !session->d3d12Delegate->lastError.empty())
        return session->d3d12Delegate->lastError.c_str();
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
