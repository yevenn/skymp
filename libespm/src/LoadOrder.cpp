#include "libespm/LoadOrder.h"
#include <stdexcept>

namespace espm {

namespace {
std::vector<FileSlot> MakeRegularSlots(size_t numFiles)
{
  std::vector<FileSlot> res(numFiles);
  for (size_t i = 0; i < numFiles; ++i) {
    res[i] = { false, static_cast<uint16_t>(i) };
  }
  return res;
}
}

LoadOrder::LoadOrder(std::initializer_list<std::string> fileNames_)
  : LoadOrder(std::vector<std::string>(fileNames_))
{
}

LoadOrder::LoadOrder(std::vector<std::string> fileNames_)
  : LoadOrder(fileNames_, MakeRegularSlots(fileNames_.size()))
{
}

LoadOrder::LoadOrder(std::vector<std::string> fileNames_,
                     std::vector<FileSlot> slots_)
  : fileNames(std::move(fileNames_))
  , slots(std::move(slots_))
{
  if (fileNames.size() != slots.size()) {
    throw std::invalid_argument(
      "espm::LoadOrder: number of slots doesn't match number of files");
  }
  IndexSlots();
}

void LoadOrder::push_back(std::string fileName)
{
  uint16_t numRegular = 0;
  for (auto& slot : slots) {
    if (!slot.isLight) {
      ++numRegular;
    }
  }
  fileNames.push_back(std::move(fileName));
  slots.push_back({ false, numRegular });
  IndexSlots();
}

FileSlot LoadOrder::GetSlot(size_t fileIndex) const noexcept
{
  return fileIndex < slots.size() ? slots[fileIndex] : FileSlot();
}

int32_t LoadOrder::FindFile(std::string_view fileName) const noexcept
{
  for (size_t i = 0; i < fileNames.size(); ++i) {
    if (fileNames[i] == fileName) {
      return static_cast<int32_t>(i);
    }
  }
  return -1;
}

int32_t LoadOrder::GetFileIndex(uint32_t formId) const noexcept
{
  FileSlot slot;
  if (!SplitCombinedId(formId, &slot, nullptr)) {
    return -1;
  }
  const auto& files = slot.isLight ? lightFiles : regularFiles;
  return slot.index < files.size() ? files[slot.index] : -1;
}

uint32_t LoadOrder::ToFormId(size_t fileIndex, uint32_t localId) const noexcept
{
  return MakeCombinedId(GetSlot(fileIndex), localId);
}

void LoadOrder::IndexSlots()
{
  regularFiles.clear();
  lightFiles.clear();
  for (size_t i = 0; i < slots.size(); ++i) {
    auto& files = slots[i].isLight ? lightFiles : regularFiles;
    if (files.size() <= slots[i].index) {
      files.resize(slots[i].index + 1, -1);
    }
    files[slots[i].index] = static_cast<int32_t>(i);
  }
}

}
