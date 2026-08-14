#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <utility>
#include <vector>

#include "codec_api.h"

namespace {

using Bytes = std::vector<uint8_t>;

struct Sample {
  Bytes data;
  uint64_t timestampUs = 0;
  bool key = false;
};

static void Append(Bytes& target, const Bytes& source) {
  target.insert(target.end(), source.begin(), source.end());
}

static void AppendU8(Bytes& target, uint8_t value) {
  target.push_back(value);
}

static void AppendU16(Bytes& target, uint16_t value) {
  target.push_back(static_cast<uint8_t>((value >> 8) & 0xff));
  target.push_back(static_cast<uint8_t>(value & 0xff));
}

static void AppendU32(Bytes& target, uint32_t value) {
  target.push_back(static_cast<uint8_t>((value >> 24) & 0xff));
  target.push_back(static_cast<uint8_t>((value >> 16) & 0xff));
  target.push_back(static_cast<uint8_t>((value >> 8) & 0xff));
  target.push_back(static_cast<uint8_t>(value & 0xff));
}

static void AppendString(Bytes& target, const char* value) {
  while (value && *value)
    target.push_back(static_cast<uint8_t>(*value++));
}

static Bytes Box(const char type[5], const Bytes& payload) {
  Bytes result;
  result.reserve(payload.size() + 8);
  AppendU32(result, static_cast<uint32_t>(payload.size() + 8));
  result.push_back(static_cast<uint8_t>(type[0]));
  result.push_back(static_cast<uint8_t>(type[1]));
  result.push_back(static_cast<uint8_t>(type[2]));
  result.push_back(static_cast<uint8_t>(type[3]));
  Append(result, payload);
  return result;
}

static Bytes Container(const char type[5], const std::vector<Bytes>& children) {
  Bytes payload;
  for (const auto& child : children)
    Append(payload, child);
  return Box(type, payload);
}

static uint8_t ClampByte(int value) {
  return static_cast<uint8_t>(std::max(0, std::min(255, value)));
}

static std::filesystem::path Utf8Path(const char* path) {
  if (!path)
    return {};
#if defined(_WIN32)
  const int count = MultiByteToWideChar(CP_UTF8, 0, path, -1, nullptr, 0);
  if (count <= 0)
    return {};
  std::wstring wide(static_cast<size_t>(count), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, path, -1, wide.data(), count);
  wide.resize(static_cast<size_t>(count - 1));
  return std::filesystem::path(wide);
#else
  return std::filesystem::path(path);
#endif
}

class SoftwareSession {
 public:
  SoftwareSession(const char* outputPath, int width, int height, int fps, int bitrate)
      : outputPath_(Utf8Path(outputPath)),
        width_(width),
        height_(height),
        fps_(std::max(1, std::min(30, fps))),
        bitrate_(std::max(128000, bitrate)) {}

  ~SoftwareSession() {
    if (encoder_) {
      encoder_->Uninitialize();
      WelsDestroySVCEncoder(encoder_);
      encoder_ = nullptr;
    }
  }

  bool Initialize() {
    if (outputPath_.empty() || width_ <= 0 || height_ <= 0 ||
        (width_ & 1) != 0 || (height_ & 1) != 0) {
      error_ = "OpenH264 requires a valid output path and positive even dimensions.";
      return false;
    }
    if (WelsCreateSVCEncoder(&encoder_) != 0 || !encoder_) {
      error_ = "WelsCreateSVCEncoder failed.";
      return false;
    }

    SEncParamExt parameters{};
    if (encoder_->GetDefaultParams(&parameters) != cmResultSuccess) {
      error_ = "OpenH264 could not provide default encoder parameters.";
      return false;
    }
    parameters.iUsageType = SCREEN_CONTENT_REAL_TIME;
    parameters.iPicWidth = width_;
    parameters.iPicHeight = height_;
    parameters.iTargetBitrate = bitrate_;
    parameters.iRCMode = RC_BITRATE_MODE;
    parameters.fMaxFrameRate = static_cast<float>(fps_);
    parameters.iTemporalLayerNum = 1;
    parameters.iSpatialLayerNum = 1;
    parameters.iComplexityMode = LOW_COMPLEXITY;
    parameters.uiIntraPeriod = static_cast<unsigned int>(fps_ * 2);
    parameters.iNumRefFrame = 1;
    parameters.iEntropyCodingModeFlag = 0;
    parameters.bEnableFrameSkip = true;
    parameters.iMultipleThreadIdc = 2;
    parameters.bEnableDenoise = false;
    parameters.bEnableBackgroundDetection = false;
    parameters.bEnableAdaptiveQuant = false;
    parameters.bEnableSceneChangeDetect = true;
    parameters.eSpsPpsIdStrategy = CONSTANT_ID;

    auto& layer = parameters.sSpatialLayers[0];
    layer.iVideoWidth = width_;
    layer.iVideoHeight = height_;
    layer.fFrameRate = static_cast<float>(fps_);
    layer.iSpatialBitrate = bitrate_;
    layer.iMaxSpatialBitrate = bitrate_;
    layer.uiProfileIdc = PRO_BASELINE;
    layer.uiLevelIdc = LEVEL_UNKNOWN;
    layer.sSliceArgument.uiSliceMode = SM_SINGLE_SLICE;

    if (encoder_->InitializeExt(&parameters) != cmResultSuccess) {
      error_ = "OpenH264 InitializeExt failed.";
      return false;
    }
    int format = videoFormatI420;
    if (encoder_->SetOption(ENCODER_OPTION_DATAFORMAT, &format) != cmResultSuccess) {
      error_ = "OpenH264 rejected I420 input format.";
      return false;
    }

    yuv_.resize(static_cast<size_t>(width_) * height_ * 3 / 2);
    initialized_ = true;
    return true;
  }

  bool AddRgba(
      const uint8_t* rgba,
      int byteCount,
      bool rowsAreBottomUp,
      double presentationSeconds) {
    if (!initialized_) {
      if (error_.empty())
        error_ = "OpenH264 was not initialized.";
      return false;
    }
    if (!rgba || byteCount != width_ * height_ * 4) {
      error_ = "OpenH264 received an invalid RGBA frame.";
      return false;
    }
    ConvertRgbaToI420(rgba, rowsAreBottomUp);

    SSourcePicture picture{};
    picture.iColorFormat = videoFormatI420;
    picture.iPicWidth = width_;
    picture.iPicHeight = height_;
    picture.iStride[0] = width_;
    picture.iStride[1] = width_ / 2;
    picture.iStride[2] = width_ / 2;
    picture.pData[0] = yuv_.data();
    picture.pData[1] = picture.pData[0] + width_ * height_;
    picture.pData[2] = picture.pData[1] + width_ * height_ / 4;
    picture.uiTimeStamp = static_cast<long long>(
        std::llround(std::max(0.0, presentationSeconds) * 1000.0));

    SFrameBSInfo bitstream{};
    if (encoder_->EncodeFrame(&picture, &bitstream) != cmResultSuccess) {
      error_ = "OpenH264 EncodeFrame failed.";
      return false;
    }
    if (bitstream.eFrameType == videoFrameTypeSkip)
      return true;

    Sample sample;
    sample.timestampUs = static_cast<uint64_t>(
        std::llround(std::max(0.0, presentationSeconds) * 1000000.0));
    sample.key = bitstream.eFrameType == videoFrameTypeIDR;
    for (int layerIndex = 0; layerIndex < bitstream.iLayerNum; ++layerIndex) {
      const auto& layerInfo = bitstream.sLayerInfo[layerIndex];
      int offset = 0;
      for (int nalIndex = 0; nalIndex < layerInfo.iNalCount; ++nalIndex) {
        const int nalBytes = layerInfo.pNalLengthInByte[nalIndex];
        if (nalBytes <= 0)
          continue;
        const uint8_t* nal = layerInfo.pBsBuf + offset;
        offset += nalBytes;
        int startCodeBytes = 0;
        if (nalBytes >= 4 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1)
          startCodeBytes = 4;
        else if (nalBytes >= 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1)
          startCodeBytes = 3;
        const int payloadBytes = nalBytes - startCodeBytes;
        if (payloadBytes <= 0)
          continue;
        const uint8_t* payload = nal + startCodeBytes;
        const int nalType = payload[0] & 0x1f;
        if (nalType == 7) {
          if (sps_.empty())
            sps_.assign(payload, payload + payloadBytes);
          continue;
        }
        if (nalType == 8) {
          if (pps_.empty())
            pps_.assign(payload, payload + payloadBytes);
          continue;
        }
        AppendU32(sample.data, static_cast<uint32_t>(payloadBytes));
        sample.data.insert(sample.data.end(), payload, payload + payloadBytes);
      }
    }
    if (!sample.data.empty())
      samples_.push_back(std::move(sample));
    return true;
  }

  bool Finish(double durationSeconds) {
    if (samples_.empty() || sps_.size() < 4 || pps_.empty()) {
      error_ = "OpenH264 produced no muxable AVC samples or parameter sets.";
      return false;
    }
    const uint64_t durationUs = static_cast<uint64_t>(std::llround(
        std::max(durationSeconds, 1.0 / static_cast<double>(fps_)) * 1000000.0));
    const Bytes ftyp = MakeFtyp();
    Bytes moov = MakeMoov(durationUs, 0);
    const uint32_t dataOffset = static_cast<uint32_t>(ftyp.size() + moov.size() + 8);
    moov = MakeMoov(durationUs, dataOffset);

    Bytes mdatPayload;
    size_t mediaBytes = 0;
    for (const auto& sample : samples_)
      mediaBytes += sample.data.size();
    mdatPayload.reserve(mediaBytes);
    for (const auto& sample : samples_)
      Append(mdatPayload, sample.data);
    const Bytes mdat = Box("mdat", mdatPayload);

    std::ofstream stream(outputPath_, std::ios::binary | std::ios::trunc);
    if (!stream) {
      error_ = "OpenH264 could not create the MP4 output file.";
      return false;
    }
    stream.write(reinterpret_cast<const char*>(ftyp.data()), static_cast<std::streamsize>(ftyp.size()));
    stream.write(reinterpret_cast<const char*>(moov.data()), static_cast<std::streamsize>(moov.size()));
    stream.write(reinterpret_cast<const char*>(mdat.data()), static_cast<std::streamsize>(mdat.size()));
    stream.flush();
    if (!stream.good()) {
      error_ = "OpenH264 could not finish writing the MP4 output file.";
      return false;
    }
    return true;
  }

  const char* LastError() const {
    return error_.empty() ? nullptr : error_.c_str();
  }

 private:
  void ConvertRgbaToI420(const uint8_t* rgba, bool rowsAreBottomUp) {
    uint8_t* yPlane = yuv_.data();
    uint8_t* uPlane = yPlane + width_ * height_;
    uint8_t* vPlane = uPlane + width_ * height_ / 4;

    for (int y = 0; y < height_; ++y) {
      const int sourceY = rowsAreBottomUp ? height_ - 1 - y : y;
      const uint8_t* source = rgba + static_cast<size_t>(sourceY) * width_ * 4;
      for (int x = 0; x < width_; ++x) {
        const int r = source[x * 4];
        const int g = source[x * 4 + 1];
        const int b = source[x * 4 + 2];
        yPlane[y * width_ + x] = ClampByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
      }
    }

    for (int y = 0; y < height_; y += 2) {
      for (int x = 0; x < width_; x += 2) {
        int r = 0;
        int g = 0;
        int b = 0;
        for (int dy = 0; dy < 2; ++dy) {
          const int sourceY = rowsAreBottomUp ? height_ - 1 - (y + dy) : y + dy;
          const uint8_t* source = rgba + static_cast<size_t>(sourceY) * width_ * 4;
          for (int dx = 0; dx < 2; ++dx) {
            r += source[(x + dx) * 4];
            g += source[(x + dx) * 4 + 1];
            b += source[(x + dx) * 4 + 2];
          }
        }
        r >>= 2;
        g >>= 2;
        b >>= 2;
        const int chromaIndex = (y / 2) * (width_ / 2) + x / 2;
        uPlane[chromaIndex] = ClampByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
        vPlane[chromaIndex] = ClampByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
      }
    }
  }

  Bytes MakeFtyp() const {
    Bytes payload;
    AppendString(payload, "isom");
    AppendU32(payload, 0x200);
    AppendString(payload, "isomiso2avc1mp41");
    return Box("ftyp", payload);
  }

  std::vector<uint32_t> SampleDurations(uint64_t totalDuration) const {
    std::vector<uint32_t> durations;
    durations.reserve(samples_.size());
    const uint64_t minimum = std::max<uint64_t>(1, 1000000ULL / static_cast<uint64_t>(fps_));
    for (size_t index = 0; index < samples_.size(); ++index) {
      const uint64_t start = samples_[index].timestampUs;
      const uint64_t end = index + 1 < samples_.size()
          ? samples_[index + 1].timestampUs
          : std::max(totalDuration, start + minimum);
      durations.push_back(static_cast<uint32_t>(std::max<uint64_t>(1, end > start ? end - start : minimum)));
    }
    return durations;
  }

  Bytes MakeAvcC() const {
    Bytes payload;
    AppendU8(payload, 1);
    AppendU8(payload, sps_[1]);
    AppendU8(payload, sps_[2]);
    AppendU8(payload, sps_[3]);
    AppendU8(payload, 0xff);
    AppendU8(payload, 0xe1);
    AppendU16(payload, static_cast<uint16_t>(sps_.size()));
    Append(payload, sps_);
    AppendU8(payload, 1);
    AppendU16(payload, static_cast<uint16_t>(pps_.size()));
    Append(payload, pps_);
    return Box("avcC", payload);
  }

  Bytes MakeMoov(uint64_t totalDuration, uint32_t dataOffset) const {
    const auto durations = SampleDurations(totalDuration);
    uint64_t actualDuration = 0;
    for (uint32_t duration : durations)
      actualDuration += duration;
    const uint32_t duration32 = static_cast<uint32_t>(std::min<uint64_t>(actualDuration, 0xffffffffULL));

    Bytes sttsPayload;
    AppendU32(sttsPayload, 0);
    AppendU32(sttsPayload, static_cast<uint32_t>(durations.size()));
    for (uint32_t duration : durations) {
      AppendU32(sttsPayload, 1);
      AppendU32(sttsPayload, duration);
    }

    Bytes stszPayload;
    AppendU32(stszPayload, 0);
    AppendU32(stszPayload, 0);
    AppendU32(stszPayload, static_cast<uint32_t>(samples_.size()));
    for (const auto& sample : samples_)
      AppendU32(stszPayload, static_cast<uint32_t>(sample.data.size()));

    Bytes stssPayload;
    AppendU32(stssPayload, 0);
    uint32_t keyCount = 0;
    for (const auto& sample : samples_)
      if (sample.key)
        ++keyCount;
    AppendU32(stssPayload, std::max<uint32_t>(1, keyCount));
    if (keyCount == 0) {
      AppendU32(stssPayload, 1);
    } else {
      for (size_t index = 0; index < samples_.size(); ++index)
        if (samples_[index].key)
          AppendU32(stssPayload, static_cast<uint32_t>(index + 1));
    }

    Bytes avc1Payload(6, 0);
    AppendU16(avc1Payload, 1);
    AppendU16(avc1Payload, 0);
    AppendU16(avc1Payload, 0);
    AppendU32(avc1Payload, 0);
    AppendU32(avc1Payload, 0);
    AppendU32(avc1Payload, 0);
    AppendU16(avc1Payload, static_cast<uint16_t>(width_));
    AppendU16(avc1Payload, static_cast<uint16_t>(height_));
    AppendU32(avc1Payload, 0x00480000);
    AppendU32(avc1Payload, 0x00480000);
    AppendU32(avc1Payload, 0);
    AppendU16(avc1Payload, 1);
    avc1Payload.resize(avc1Payload.size() + 32, 0);
    AppendU16(avc1Payload, 0x0018);
    AppendU16(avc1Payload, 0xffff);
    Append(avc1Payload, MakeAvcC());
    Bytes btrtPayload;
    AppendU32(btrtPayload, static_cast<uint32_t>(bitrate_));
    AppendU32(btrtPayload, static_cast<uint32_t>(bitrate_));
    AppendU32(btrtPayload, static_cast<uint32_t>(bitrate_));
    Append(avc1Payload, Box("btrt", btrtPayload));

    Bytes stsdPayload;
    AppendU32(stsdPayload, 0);
    AppendU32(stsdPayload, 1);
    Append(stsdPayload, Box("avc1", avc1Payload));

    Bytes stscPayload;
    AppendU32(stscPayload, 0);
    AppendU32(stscPayload, 1);
    AppendU32(stscPayload, 1);
    AppendU32(stscPayload, static_cast<uint32_t>(samples_.size()));
    AppendU32(stscPayload, 1);

    Bytes stcoPayload;
    AppendU32(stcoPayload, 0);
    AppendU32(stcoPayload, 1);
    AppendU32(stcoPayload, dataOffset);

    const Bytes stbl = Container("stbl", {
        Box("stsd", stsdPayload), Box("stts", sttsPayload), Box("stsc", stscPayload),
        Box("stsz", stszPayload), Box("stco", stcoPayload), Box("stss", stssPayload)});

    Bytes vmhdPayload;
    AppendU32(vmhdPayload, 1);
    AppendU16(vmhdPayload, 0);
    AppendU16(vmhdPayload, 0);
    AppendU16(vmhdPayload, 0);
    AppendU16(vmhdPayload, 0);

    Bytes urlPayload;
    AppendU32(urlPayload, 1);
    Bytes drefPayload;
    AppendU32(drefPayload, 0);
    AppendU32(drefPayload, 1);
    Append(drefPayload, Box("url ", urlPayload));
    const Bytes dinf = Container("dinf", {Box("dref", drefPayload)});
    const Bytes minf = Container("minf", {Box("vmhd", vmhdPayload), dinf, stbl});

    Bytes mdhdPayload;
    AppendU32(mdhdPayload, 0);
    AppendU32(mdhdPayload, 0);
    AppendU32(mdhdPayload, 0);
    AppendU32(mdhdPayload, 1000000);
    AppendU32(mdhdPayload, duration32);
    AppendU16(mdhdPayload, 0x55c4);
    AppendU16(mdhdPayload, 0);

    Bytes hdlrPayload;
    AppendU32(hdlrPayload, 0);
    AppendU32(hdlrPayload, 0);
    AppendString(hdlrPayload, "vide");
    AppendU32(hdlrPayload, 0);
    AppendU32(hdlrPayload, 0);
    AppendU32(hdlrPayload, 0);
    AppendString(hdlrPayload, "VideoHandler");
    AppendU8(hdlrPayload, 0);
    const Bytes mdia = Container("mdia", {Box("mdhd", mdhdPayload), Box("hdlr", hdlrPayload), minf});

    Bytes tkhdPayload;
    AppendU32(tkhdPayload, 3);
    AppendU32(tkhdPayload, 0);
    AppendU32(tkhdPayload, 0);
    AppendU32(tkhdPayload, 1);
    AppendU32(tkhdPayload, 0);
    AppendU32(tkhdPayload, duration32);
    AppendU32(tkhdPayload, 0);
    AppendU32(tkhdPayload, 0);
    AppendU16(tkhdPayload, 0);
    AppendU16(tkhdPayload, 0);
    AppendU16(tkhdPayload, 0);
    AppendU16(tkhdPayload, 0);
    const uint32_t matrix[] = {
        0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000};
    for (uint32_t value : matrix)
      AppendU32(tkhdPayload, value);
    AppendU32(tkhdPayload, static_cast<uint32_t>(width_ << 16));
    AppendU32(tkhdPayload, static_cast<uint32_t>(height_ << 16));
    const Bytes trak = Container("trak", {Box("tkhd", tkhdPayload), mdia});

    Bytes mvhdPayload;
    AppendU32(mvhdPayload, 0);
    AppendU32(mvhdPayload, 0);
    AppendU32(mvhdPayload, 0);
    AppendU32(mvhdPayload, 1000000);
    AppendU32(mvhdPayload, duration32);
    AppendU32(mvhdPayload, 0x00010000);
    AppendU16(mvhdPayload, 0x0100);
    AppendU16(mvhdPayload, 0);
    AppendU32(mvhdPayload, 0);
    AppendU32(mvhdPayload, 0);
    for (uint32_t value : matrix)
      AppendU32(mvhdPayload, value);
    for (int index = 0; index < 6; ++index)
      AppendU32(mvhdPayload, 0);
    AppendU32(mvhdPayload, 2);
    return Container("moov", {Box("mvhd", mvhdPayload), trak});
  }

  std::filesystem::path outputPath_;
  int width_ = 0;
  int height_ = 0;
  int fps_ = 1;
  int bitrate_ = 128000;
  ISVCEncoder* encoder_ = nullptr;
  bool initialized_ = false;
  Bytes yuv_;
  Bytes sps_;
  Bytes pps_;
  std::vector<Sample> samples_;
  std::string error_;
};

}  // namespace

#if defined(_WIN32)
#define MACACA_BEACON_EXPORT extern "C" __declspec(dllexport)
#else
#define MACACA_BEACON_EXPORT extern "C" __attribute__((visibility("default")))
#endif

MACACA_BEACON_EXPORT int MacacaBeaconWindowsVideo_SoftwareIsAvailable() {
  ISVCEncoder* encoder = nullptr;
  const int result = WelsCreateSVCEncoder(&encoder);
  const bool available = result == 0 && encoder != nullptr;
  if (encoder)
    WelsDestroySVCEncoder(encoder);
  return available ? 1 : 0;
}

MACACA_BEACON_EXPORT void* MacacaBeaconWindowsVideo_SoftwareCreate(
    const char* outputPath,
    int width,
    int height,
    int framesPerSecond,
    int bitrate) {
  auto* session = new SoftwareSession(outputPath, width, height, framesPerSecond, bitrate);
  session->Initialize();
  return session;
}

MACACA_BEACON_EXPORT int MacacaBeaconWindowsVideo_SoftwareAddRgba(
    void* session,
    const uint8_t* rgba,
    int byteCount,
    int rowsAreBottomUp,
    double presentationSeconds) {
  return session && static_cast<SoftwareSession*>(session)->AddRgba(
      rgba, byteCount, rowsAreBottomUp != 0, presentationSeconds) ? 1 : 0;
}

MACACA_BEACON_EXPORT int MacacaBeaconWindowsVideo_SoftwareFinish(
    void* session,
    double durationSeconds) {
  return session && static_cast<SoftwareSession*>(session)->Finish(durationSeconds) ? 1 : 0;
}

MACACA_BEACON_EXPORT const char* MacacaBeaconWindowsVideo_SoftwareLastError(void* session) {
  return session ? static_cast<SoftwareSession*>(session)->LastError() : nullptr;
}

MACACA_BEACON_EXPORT void MacacaBeaconWindowsVideo_SoftwareDestroy(void* session) {
  delete static_cast<SoftwareSession*>(session);
}
