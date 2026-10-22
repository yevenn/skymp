#pragma once
#include <array>
#include <cstdint>

namespace espm {

// Light plugins (ESL) don't get their own top byte. They all share FE and are
// told apart by a 12-bit index, leaving 12 bits for the record itself:
// FE xxx yyy, where xxx is the light index and yyy the record's local id.
constexpr uint32_t kLightPluginPrefix = 0xFE000000;
constexpr uint32_t kMaxLightPlugins = 0x1000;
constexpr uint32_t kMaxRegularPlugins = 0xFE;

// Where a plugin's records live in combined (load order) form ids
struct FileSlot
{
  bool isLight = false;
  // Top byte for regular plugins, 12-bit light index for light plugins
  uint16_t index = 0;

  friend bool operator==(const FileSlot& left, const FileSlot& right)
  {
    return left.isLight == right.isLight && left.index == right.index;
  }
};

// Combined form id of a record with the given local id in the file at `slot`
uint32_t MakeCombinedId(FileSlot slot, uint32_t localId) noexcept;

// Splits a combined form id into file slot and local id. Returns false for
// ids that don't belong to any plugin (FF xxxxxx, created at runtime)
bool SplitCombinedId(uint32_t combinedId, FileSlot* outSlot,
                     uint32_t* outLocalId) noexcept;

// Translates form ids between a plugin's own numbering, where the top byte
// indexes the plugin's master list (and the plugin itself after its masters),
// and combined load order numbering
class IdMapping
{
public:
  enum class Direction
  {
    kToCombined,
    kToRaw
  };

  explicit IdMapping(Direction direction);

  // Position `rawIdx` in the plugin's master list refers to the file at `slot`
  void Add(uint8_t rawIdx, FileSlot slot) noexcept;

  // Returns an id >= 0xff000000 if the id can't be mapped
  uint32_t Map(uint32_t id) const noexcept;

private:
  static constexpr int16_t kUnmapped = -1;

  Direction direction;

  // kToCombined
  std::array<FileSlot, 256> rawToSlot;
  std::array<bool, 256> rawIsMapped{};

  // kToRaw
  std::array<int16_t, 256> regularToRaw;
  std::array<int16_t, kMaxLightPlugins> lightToRaw;
};

}
