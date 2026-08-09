#include <stddef.h>

#import <AVFoundation/AVFoundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#import <TargetConditionals.h>
#import <VideoToolbox/VideoToolbox.h>
#import <dispatch/dispatch.h>
#import <Metal/Metal.h>

#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <string>

struct MacacaBeaconVideoSession
{
    __strong AVAssetWriter* writer = nil;
    __strong AVAssetWriterInput* input = nil;
    __strong AVAssetWriterInputPixelBufferAdaptor* adaptor = nil;
    std::string lastError;
    int width = 0;
    int height = 0;
    int appendedFrames = 0;
    double lastPresentationSeconds = -1.0;
    bool ready = false;
    dispatch_group_t pendingGpuEvents = nil;
    dispatch_group_t pendingGpuFrames = nil;
    dispatch_queue_t gpuEncodeQueue = nil;
    std::atomic<bool> finishStarted{false};
    std::atomic<bool> finishDone{false};
    std::atomic<bool> finishSucceeded{false};
};

struct MacacaBeaconGpuSubmit
{
    void* session = nullptr;
    void* nativeTexture = nullptr;
    double presentationSeconds = 0.0;
};

typedef void (*MacacaBeaconUnityRenderingEventAndData)(int, void*);

static void SetError(MacacaBeaconVideoSession* session, NSString* message)
{
    if (session == nullptr)
        return;
    session->lastError = message == nil ? "Unknown AVAssetWriter error." : message.UTF8String;
}
static void SetWriterError(MacacaBeaconVideoSession* session, NSString* fallback)
{
    NSError* error = session == nullptr ? nil : session->writer.error;
    NSString* message = error != nil
        ? [NSString stringWithFormat:@"%@ (%@ %ld): %@", error.localizedDescription, error.domain, (long)error.code, error.localizedFailureReason ?: @""]
        : fallback;
    SetError(session, message);
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_IsAvailable()
{
    return 1;
}

static void* CreateSession(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate,
    bool gpuTextureSession)
{
    @autoreleasepool
    {
        auto* session = new MacacaBeaconVideoSession();
        session->pendingGpuEvents = dispatch_group_create();
        session->pendingGpuFrames = dispatch_group_create();
        session->gpuEncodeQueue = dispatch_queue_create("com.macacagames.beacon.gpu-encode", DISPATCH_QUEUE_SERIAL);
        session->width = std::max(2, width - (width % 2));
        session->height = std::max(2, height - (height % 2));

        if (outputPath == nullptr || outputPath[0] == '\0')
        {
            SetError(session, @"The MP4 output path is empty.");
            return session;
        }

        NSString* path = [NSString stringWithUTF8String:outputPath];
        NSURL* url = [NSURL fileURLWithPath:path];
        [[NSFileManager defaultManager] removeItemAtURL:url error:nil];

        NSError* error = nil;
        session->writer = [[AVAssetWriter alloc] initWithURL:url fileType:AVFileTypeMPEG4 error:&error];
        if (session->writer == nil)
        {
            SetError(session, error.localizedDescription ?: @"Could not create AVAssetWriter.");
            return session;
        }
        session->writer.shouldOptimizeForNetworkUse = YES;

        NSDictionary* compression = @{
            AVVideoAverageBitRateKey: @(std::max(128000, bitrate)),
            AVVideoMaxKeyFrameIntervalKey: @(std::max(1, framesPerSecond)),
            AVVideoAllowFrameReorderingKey: @NO,
            AVVideoProfileLevelKey: AVVideoProfileLevelH264BaselineAutoLevel
        };
#if TARGET_OS_OSX
        NSDictionary* settings = @{
            AVVideoCodecKey: AVVideoCodecTypeH264,
            AVVideoWidthKey: @(session->width),
            AVVideoHeightKey: @(session->height),
            AVVideoCompressionPropertiesKey: compression
        };
#else
        // iOS devices are optimized for the hardware H.264 path. Do not disable it as
        // we do on macOS, where software encoding is more predictable in Editor builds.
        NSDictionary* settings = @{
            AVVideoCodecKey: AVVideoCodecTypeH264,
            AVVideoWidthKey: @(session->width),
            AVVideoHeightKey: @(session->height),
            AVVideoCompressionPropertiesKey: compression
        };
#endif

        session->input = [[AVAssetWriterInput alloc] initWithMediaType:AVMediaTypeVideo outputSettings:settings];
        session->input.expectsMediaDataInRealTime = YES;
        if (![session->writer canAddInput:session->input])
        {
            SetError(session, @"AVAssetWriter rejected its H.264 video input.");
            return session;
        }
        [session->writer addInput:session->input];

        if (gpuTextureSession)
        {
            // ScreenCapture's Metal render target is presented upside down in
            // the macOS Editor path. Store the correction as video metadata so
            // the pixel copy itself remains a fast GPU blit.
            session->input.transform = CGAffineTransformMake(-1.0, 0.0, 0.0, -1.0,
                                                               session->width, session->height);
        }

        NSDictionary* pixelBufferAttributes = @{
            (NSString*)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
            (NSString*)kCVPixelBufferWidthKey: @(session->width),
            (NSString*)kCVPixelBufferHeightKey: @(session->height),
            (NSString*)kCVPixelBufferIOSurfacePropertiesKey: @{}
        };
        session->adaptor = [[AVAssetWriterInputPixelBufferAdaptor alloc]
            initWithAssetWriterInput:session->input
            sourcePixelBufferAttributes:pixelBufferAttributes];

        if (![session->writer startWriting])
        {
            SetWriterError(session, @"AVAssetWriter could not start writing.");
            return session;
        }
        [session->writer startSessionAtSourceTime:kCMTimeZero];
        session->ready = true;
        return session;
    }
}

extern "C" __attribute__((visibility("default"))) void* MacacaBeaconVideo_Create(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate)
{
    return CreateSession(outputPath, width, height, framesPerSecond, bitrate, false);
}

extern "C" __attribute__((visibility("default"))) void* MacacaBeaconVideo_GpuCreate(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate)
{
    return CreateSession(outputPath, width, height, framesPerSecond, bitrate, true);
}

static BOOL AppendMetalPixelBuffer(MacacaBeaconVideoSession* session, CVPixelBufferRef pixelBuffer, double presentationSeconds)
{
    for (int attempt = 0; attempt < 500 && !session->input.readyForMoreMediaData; ++attempt)
        [NSThread sleepForTimeInterval:0.002];
    if (!session->input.readyForMoreMediaData)
    {
        SetError(session, @"The H.264 encoder did not become ready for a GPU frame.");
        return NO;
    }

    CMTime presentationTime = CMTimeMakeWithSeconds(std::max(0.0, presentationSeconds), 60000);
    if (![session->adaptor appendPixelBuffer:pixelBuffer withPresentationTime:presentationTime])
    {
        SetWriterError(session, @"AVAssetWriter rejected a GPU pixel buffer.");
        return NO;
    }
    session->lastPresentationSeconds = presentationSeconds;
    session->appendedFrames++;
    return YES;
}

static void SubmitMetalTexture(MacacaBeaconGpuSubmit* submit)
{
    if (submit == nullptr || submit->session == nullptr || submit->nativeTexture == nullptr)
        return;

    auto* session = static_cast<MacacaBeaconVideoSession*>(submit->session);
    if (!session->ready)
        return;

    id<MTLTexture> sourceTexture = (__bridge id<MTLTexture>)submit->nativeTexture;
    if (sourceTexture == nil || sourceTexture.width != (NSUInteger)session->width ||
        sourceTexture.height != (NSUInteger)session->height)
    {
        SetError(session, @"GPU capture texture dimensions do not match the encoder.");
        return;
    }

    // Unity's macOS capture textures are explicitly created as BGRA32. Both
    // BGRA8Unorm variants have BGRA byte storage; the sRGB variant only changes
    // shader sampling/writing semantics. CVPixelBuffer's 32BGRA format expects
    // those same bytes, so the correct transfer is a byte-for-byte GPU blit.
    // A shader-level .bgra swizzle would exchange red and blue a second time.
    const MTLPixelFormat sourcePixelFormat = sourceTexture.pixelFormat;
    if (sourcePixelFormat != MTLPixelFormatBGRA8Unorm &&
        sourcePixelFormat != MTLPixelFormatBGRA8Unorm_sRGB)
    {
        SetError(session, [NSString stringWithFormat:@"Unsupported macOS GPU capture pixel format: %lu (expected BGRA8Unorm or BGRA8Unorm_sRGB).",
                                                      (unsigned long)sourcePixelFormat]);
        return;
    }

    CVPixelBufferRef pixelBuffer = nullptr;
    if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, session->adaptor.pixelBufferPool, &pixelBuffer) != kCVReturnSuccess || pixelBuffer == nullptr)
    {
        SetError(session, @"Could not allocate an IOSurface-backed GPU video pixel buffer.");
        return;
    }

    IOSurfaceRef surface = CVPixelBufferGetIOSurface(pixelBuffer);
    if (surface == nullptr)
    {
        CVPixelBufferRelease(pixelBuffer);
        SetError(session, @"The video pixel buffer has no IOSurface for Metal interop.");
        return;
    }

    id<MTLTexture> destinationTexture = [sourceTexture.device
        newTextureWithDescriptor:(^{
            MTLTextureDescriptor* descriptor = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:sourcePixelFormat
                                                                                                  width:session->width
                                                                                                 height:session->height
                                                                                              mipmapped:NO];
            descriptor.storageMode = MTLStorageModeShared;
            descriptor.usage = MTLTextureUsageUnknown;
            return descriptor;
        }())
        iosurface:surface
        plane:0];
    if (destinationTexture == nil)
    {
        CVPixelBufferRelease(pixelBuffer);
        SetError(session, @"Metal could not create a texture view of the video IOSurface.");
        return;
    }

    id<MTLCommandQueue> queue = [sourceTexture.device newCommandQueue];
    id<MTLCommandBuffer> commandBuffer = [queue commandBuffer];
    id<MTLBlitCommandEncoder> blit = [commandBuffer blitCommandEncoder];
    const MTLSize copySize = MTLSizeMake(session->width, session->height, 1);
    [blit copyFromTexture:sourceTexture
              sourceSlice:0
              sourceLevel:0
             sourceOrigin:MTLOriginMake(0, 0, 0)
               sourceSize:copySize
                toTexture:destinationTexture
         destinationSlice:0
         destinationLevel:0
        destinationOrigin:MTLOriginMake(0, 0, 0)];
    [blit endEncoding];

    dispatch_group_enter(session->pendingGpuFrames);
    const double presentationSeconds = submit->presentationSeconds;
    [commandBuffer addCompletedHandler:^(id<MTLCommandBuffer> completedCommandBuffer) {
        dispatch_async(session->gpuEncodeQueue, ^{
            if (completedCommandBuffer.status == MTLCommandBufferStatusCompleted)
                AppendMetalPixelBuffer(session, pixelBuffer, presentationSeconds);
            else
                SetError(session, @"Metal could not complete the GPU capture blit.");
            CVPixelBufferRelease(pixelBuffer);
            dispatch_group_leave(session->pendingGpuFrames);
        });
    }];
    [commandBuffer commit];
}

static void MacacaBeaconVideo_RenderEvent(int eventId, void* data)
{
    if (eventId != 1)
        return;
    auto* submit = static_cast<MacacaBeaconGpuSubmit*>(data);
    auto* session = submit == nullptr ? nullptr : static_cast<MacacaBeaconVideoSession*>(submit->session);
    SubmitMetalTexture(submit);
    if (session != nullptr)
        dispatch_group_leave(session->pendingGpuEvents);
    std::free(data);
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_GpuIsAvailable()
{
    return MTLCreateSystemDefaultDevice() == nil ? 0 : 1;
}

extern "C" __attribute__((visibility("default"))) void* MacacaBeaconVideo_GpuGetRenderEventFunc()
{
    return reinterpret_cast<void*>(&MacacaBeaconVideo_RenderEvent);
}

extern "C" __attribute__((visibility("default"))) void* MacacaBeaconVideo_GpuAllocateSubmitData(
    void* session,
    void* nativeTexture,
    double presentationSeconds)
{
    auto* submit = static_cast<MacacaBeaconGpuSubmit*>(std::malloc(sizeof(MacacaBeaconGpuSubmit)));
    if (submit == nullptr)
        return nullptr;
    auto* videoSession = static_cast<MacacaBeaconVideoSession*>(session);
    if (videoSession == nullptr || !videoSession->ready)
    {
        std::free(submit);
        return nullptr;
    }
    dispatch_group_enter(videoSession->pendingGpuEvents);
    submit->session = session;
    submit->nativeTexture = nativeTexture;
    submit->presentationSeconds = presentationSeconds;
    return submit;
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_AddJpeg(
    void* handle,
    const unsigned char* jpegBytes,
    int byteCount,
    double presentationSeconds)
{
    @autoreleasepool
    {
        auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
        if (session == nullptr || !session->ready || jpegBytes == nullptr || byteCount <= 0)
            return 0;
        if (presentationSeconds + 0.000001 < session->lastPresentationSeconds)
        {
            SetError(session, @"Video frame timestamps must be monotonic.");
            return 0;
        }

        NSData* data = [NSData dataWithBytes:jpegBytes length:(NSUInteger)byteCount];
        CGImageSourceRef source = CGImageSourceCreateWithData((__bridge CFDataRef)data, nullptr);
        if (source == nullptr)
        {
            SetError(session, @"ImageIO could not decode a captured JPEG frame.");
            return 0;
        }
        CGImageRef image = CGImageSourceCreateImageAtIndex(source, 0, nullptr);
        CFRelease(source);
        if (image == nullptr)
        {
            SetError(session, @"ImageIO returned an empty JPEG image.");
            return 0;
        }

        CVPixelBufferRef pixelBuffer = nullptr;
        CVReturn pixelResult = CVPixelBufferPoolCreatePixelBuffer(
            kCFAllocatorDefault,
            session->adaptor.pixelBufferPool,
            &pixelBuffer);
        if (pixelResult != kCVReturnSuccess || pixelBuffer == nullptr)
        {
            CGImageRelease(image);
            SetError(session, @"Could not allocate a video pixel buffer.");
            return 0;
        }

        CVPixelBufferLockBaseAddress(pixelBuffer, 0);
        void* baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer);
        size_t bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer);
        CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
        CGContextRef context = CGBitmapContextCreate(
            baseAddress,
            (size_t)session->width,
            (size_t)session->height,
            8,
            bytesPerRow,
            colorSpace,
            kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst);
        CGColorSpaceRelease(colorSpace);
        if (context == nullptr)
        {
            CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);
            CVPixelBufferRelease(pixelBuffer);
            CGImageRelease(image);
            SetError(session, @"Could not create a CoreGraphics video frame context.");
            return 0;
        }

        CGContextSetRGBFillColor(context, 0, 0, 0, 1);
        CGContextFillRect(context, CGRectMake(0, 0, session->width, session->height));
        CGContextSetInterpolationQuality(context, kCGInterpolationHigh);
        CGContextDrawImage(context, CGRectMake(0, 0, session->width, session->height), image);
        CGContextRelease(context);
        CGImageRelease(image);
        CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);

        for (int attempt = 0; attempt < 500 && !session->input.readyForMoreMediaData; ++attempt)
            [NSThread sleepForTimeInterval:0.002];
        if (!session->input.readyForMoreMediaData)
        {
            CVPixelBufferRelease(pixelBuffer);
            SetError(session, @"The H.264 encoder did not become ready for more frames.");
            return 0;
        }

        CMTime presentationTime = CMTimeMakeWithSeconds(std::max(0.0, presentationSeconds), 60000);
        BOOL appended = [session->adaptor appendPixelBuffer:pixelBuffer withPresentationTime:presentationTime];
        CVPixelBufferRelease(pixelBuffer);
        if (!appended)
        {
            SetWriterError(session, @"AVAssetWriter rejected a pixel buffer.");
            return 0;
        }

        session->lastPresentationSeconds = presentationSeconds;
        session->appendedFrames++;
        return 1;
    }
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_AddRgba(
    void* handle,
    const unsigned char* rgbaBytes,
    int byteCount,
    int sourceWidth,
    int sourceHeight,
    double presentationSeconds)
{
    @autoreleasepool
    {
        auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
        if (session == nullptr || !session->ready || rgbaBytes == nullptr ||
            sourceWidth <= 0 || sourceHeight <= 0 ||
            byteCount < sourceWidth * sourceHeight * 4)
            return 0;
        if (presentationSeconds + 0.000001 < session->lastPresentationSeconds)
        {
            SetError(session, @"Video frame timestamps must be monotonic.");
            return 0;
        }

        CVPixelBufferRef pixelBuffer = nullptr;
        CVReturn pixelResult = CVPixelBufferPoolCreatePixelBuffer(
            kCFAllocatorDefault, session->adaptor.pixelBufferPool, &pixelBuffer);
        if (pixelResult != kCVReturnSuccess || pixelBuffer == nullptr)
        {
            SetError(session, @"Could not allocate a raw video pixel buffer.");
            return 0;
        }

        CVPixelBufferLockBaseAddress(pixelBuffer, 0);
        auto* destination = static_cast<unsigned char*>(CVPixelBufferGetBaseAddress(pixelBuffer));
        const size_t destinationStride = CVPixelBufferGetBytesPerRow(pixelBuffer);
        for (int y = 0; y < session->height; ++y)
        {
            const int sourceY = std::min(sourceHeight - 1, y * sourceHeight / session->height);
            auto* destinationRow = destination + static_cast<size_t>(y) * destinationStride;
            for (int x = 0; x < session->width; ++x)
            {
                const int sourceX = std::min(sourceWidth - 1, x * sourceWidth / session->width);
                const auto* source = rgbaBytes + (static_cast<size_t>(sourceY) * sourceWidth + sourceX) * 4;
                auto* pixel = destinationRow + static_cast<size_t>(x) * 4;
                pixel[0] = source[2];
                pixel[1] = source[1];
                pixel[2] = source[0];
                pixel[3] = 255;
            }
        }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);

        for (int attempt = 0; attempt < 500 && !session->input.readyForMoreMediaData; ++attempt)
            [NSThread sleepForTimeInterval:0.002];
        if (!session->input.readyForMoreMediaData)
        {
            CVPixelBufferRelease(pixelBuffer);
            SetError(session, @"The H.264 encoder did not become ready for a raw frame.");
            return 0;
        }

        CMTime presentationTime = CMTimeMakeWithSeconds(std::max(0.0, presentationSeconds), 60000);
        BOOL appended = [session->adaptor appendPixelBuffer:pixelBuffer withPresentationTime:presentationTime];
        CVPixelBufferRelease(pixelBuffer);
        if (!appended)
        {
            SetWriterError(session, @"AVAssetWriter rejected a raw pixel buffer.");
            return 0;
        }

        session->lastPresentationSeconds = presentationSeconds;
        session->appendedFrames++;
        return 1;
    }
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_Finish(void* handle)
{
    @autoreleasepool
    {
        auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
        if (session == nullptr)
            return 0;

        // A render event may still be queued in Unity even though no Metal
        // command buffer has been submitted yet. Wait for both stages before
        // touching the session or its AVAssetWriter.
        dispatch_group_wait(session->pendingGpuEvents, DISPATCH_TIME_FOREVER);
        dispatch_group_wait(session->pendingGpuFrames, DISPATCH_TIME_FOREVER);
        if (session->appendedFrames == 0)
            return 0;

        [session->input markAsFinished];
        dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
        [session->writer finishWritingWithCompletionHandler:^{
            dispatch_semaphore_signal(semaphore);
        }];
        dispatch_semaphore_wait(semaphore, DISPATCH_TIME_FOREVER);

        if (session->writer.status != AVAssetWriterStatusCompleted)
        {
            SetWriterError(session, @"AVAssetWriter did not complete the MP4 file.");
            return 0;
        }
        return 1;
    }
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_BeginFinish(void* handle)
{
    auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
    if (session == nullptr || !session->ready)
        return 0;

    bool expected = false;
    if (!session->finishStarted.compare_exchange_strong(expected, true))
        return 1;

    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        // The GPU completion callback queues the final pixel-buffer append on
        // gpuEncodeQueue. Waiting here keeps the Unity/render thread free while
        // still guaranteeing that AVAssetWriter sees every submitted frame.
        dispatch_group_wait(session->pendingGpuEvents, DISPATCH_TIME_FOREVER);
        dispatch_group_wait(session->pendingGpuFrames, DISPATCH_TIME_FOREVER);

        bool succeeded = false;
        if (session->appendedFrames > 0)
        {
            [session->input markAsFinished];
            dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
            [session->writer finishWritingWithCompletionHandler:^{
                dispatch_semaphore_signal(semaphore);
            }];
            dispatch_semaphore_wait(semaphore, DISPATCH_TIME_FOREVER);
            succeeded = session->writer.status == AVAssetWriterStatusCompleted;
            if (!succeeded)
                SetWriterError(session, @"AVAssetWriter did not complete the MP4 file.");
        }

        session->finishSucceeded.store(succeeded);
        session->finishDone.store(true);
    });
    return 1;
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_IsFinishDone(void* handle)
{
    auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
    return session != nullptr && session->finishDone.load() ? 1 : 0;
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_FinishSucceeded(void* handle)
{
    auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
    return session != nullptr && session->finishSucceeded.load() ? 1 : 0;
}

extern "C" __attribute__((visibility("default"))) const char* MacacaBeaconVideo_LastError(void* handle)
{
    auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
    return session == nullptr ? "Invalid video encoder session." : session->lastError.c_str();
}

extern "C" __attribute__((visibility("default"))) void MacacaBeaconVideo_Destroy(void* handle)
{
    auto* session = static_cast<MacacaBeaconVideoSession*>(handle);
    delete session;
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_GetBuildNumber()
{
    @autoreleasepool
    {
        NSString* buildNumber = [[NSBundle mainBundle] objectForInfoDictionaryKey:@"CFBundleVersion"];
        return buildNumber == nil ? -1 : [buildNumber intValue];
    }
}

extern "C" __attribute__((visibility("default"))) int MacacaBeaconVideo_ConcatSegments(
    const char* outputPath,
    const char* const* inputPaths,
    int inputCount)
{
    @autoreleasepool
    {
        if (outputPath == nullptr || outputPath[0] == '\0' || inputPaths == nullptr || inputCount <= 0)
            return 0;

        NSString* output = [NSString stringWithUTF8String:outputPath];
        if (output == nil)
            return 0;
        NSURL* outputUrl = [NSURL fileURLWithPath:output];
        [[NSFileManager defaultManager] removeItemAtURL:outputUrl error:nil];

        AVMutableComposition* composition = [AVMutableComposition composition];
        AVMutableCompositionTrack* compositionTrack = [composition addMutableTrackWithMediaType:AVMediaTypeVideo
                                                                                preferredTrackID:kCMPersistentTrackID_Invalid];
        CMTime cursor = kCMTimeZero;
        NSError* error = nil;
        for (int index = 0; index < inputCount; ++index)
        {
            if (inputPaths[index] == nullptr)
                return 0;
            NSString* input = [NSString stringWithUTF8String:inputPaths[index]];
            AVAsset* asset = [AVAsset assetWithURL:[NSURL fileURLWithPath:input]];
            AVAssetTrack* track = [[asset tracksWithMediaType:AVMediaTypeVideo] firstObject];
            if (track == nil || asset.duration.value <= 0)
                return 0;
            if (![compositionTrack insertTimeRange:CMTimeRangeMake(kCMTimeZero, asset.duration)
                                           ofTrack:track
                                            atTime:cursor
                                             error:&error])
                return 0;
            cursor = CMTimeAdd(cursor, asset.duration);
        }

        AVAssetExportSession* exporter = [[AVAssetExportSession alloc]
            initWithAsset:composition
            presetName:AVAssetExportPresetPassthrough];
        if (exporter == nil || ![[exporter supportedFileTypes] containsObject:AVFileTypeMPEG4])
            return 0;
        exporter.outputURL = outputUrl;
        exporter.outputFileType = AVFileTypeMPEG4;
        exporter.shouldOptimizeForNetworkUse = YES;

        dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
        [exporter exportAsynchronouslyWithCompletionHandler:^{
            dispatch_semaphore_signal(semaphore);
        }];
        dispatch_semaphore_wait(semaphore, DISPATCH_TIME_FOREVER);
        return exporter.status == AVAssetExportSessionStatusCompleted ? 1 : 0;
    }
}
