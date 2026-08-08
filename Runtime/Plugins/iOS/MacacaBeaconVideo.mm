#include <stddef.h>

#import <AVFoundation/AVFoundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#import <TargetConditionals.h>
#import <VideoToolbox/VideoToolbox.h>
#import <dispatch/dispatch.h>

#include <algorithm>
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
};

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

extern "C" __attribute__((visibility("default"))) void* MacacaBeaconVideo_Create(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate)
{
    @autoreleasepool
    {
        auto* session = new MacacaBeaconVideoSession();
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
        if (session == nullptr || session->appendedFrames == 0)
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
