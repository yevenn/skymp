#include "libespm/IdMapping.h"

namespace espm {

namespace {
constexpr uint32_t kUnmappedPrefix = 0xFF000000;
constexpr uint32_t kRegularLocalIdMask = 0x00FFFFFF;
constexpr uint32_t kLightLocalIdMask = 0x00000FFF;

uint32_t Unmapped(uint32_t id) noexcept
{
  return kUnmappedPrefix | (id & kRegularLocalIdMask);
}
}

uint32_t MakeCombinedId(FileSlot slot, uint32_t localId) noexcept
{
  if (slot.isLight) {
    return kLightPluginPrefix | (static_cast<uint32_t>(slot.index) << 12) |
      (localId & kLightLocalIdMask);
  }
  return (static_cast<uint32_t>(slot.index) << 24) |
    (localId & kRegularLocalIdMask);
}

bool SplitCombinedId(uint32_t combinedId, FileSlot* outSlot,
                     uint32_t* outLocalId) noexcept
{
  const uint32_t topByte = combinedId >> 24;
  if (topByte == 0xFF) {
    return false;
  }

  FileSlot slot;
  uint32_t localId;
  if (topByte == (kLightPluginPrefix >> 24)) {
    slot = { true, static_cast<uint16_t>((combinedId >> 12) & 0xFFF) };
    localId = combinedId & kLightLocalIdMask;
  } else {
    slot = { false, static_cast<uint16_t>(topByte) };
    localId = combinedId & kRegularLocalIdMask;
  }

  if (outSlot) {
    *outSlot = slot;
  }
  if (outLocalId) {
    *outLocalId = localId;
  }
  return true;
}

IdMapping::IdMapping(Direction direction_)
  : direction(direction_)
{
  regularToRaw.fill(kUnmapped);
  lightToRaw.fill(kUnmapped);
}

void IdMapping::Add(uint8_t rawIdx, FileSlot slot) noexcept
{
  rawToSlot[rawIdx] = slot;
  rawIsMapped[rawIdx] = true;

  auto& toRaw = slot.isLight ? lightToRaw[slot.index % kMaxLightPlugins]
                             : regularToRaw[slot.index % 256];
  toRaw = rawIdx;
}

uint32_t IdMapping::Map(uint32_t id) const noexcept
{
  if (direction == Direction::kToCombined) {
    const uint8_t rawIdx = static_cast<uint8_t>(id >> 24);
    if (!rawIsMapped[rawIdx]) {
      return Unmapped(id);
    }
    return MakeCombinedId(rawToSlot[rawIdx], id & kRegularLocalIdMask);
  }

  FileSlot slot;
  uint32_t localId;
  if (!SplitCombinedId(id, &slot, &localId)) {
    return Unmapped(id);
  }

  const int16_t rawIdx =
    slot.isLight ? lightToRaw[slot.index] : regularToRaw[slot.index];
  if (rawIdx == kUnmapped) {
    return Unmapped(id);
  }
  return (static_cast<uint32_t>(rawIdx) << 24) | localId;
}

}
