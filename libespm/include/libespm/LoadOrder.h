#pragma once
#include "IdMapping.h"
#include <cstdint>
#include <initializer_list>
#include <string>
#include <string_view>
#include <vector>

namespace espm {

// Plugin file names in load order, together with each file's slot in combined
// form ids. Regular plugins own a top byte, light plugins share FE (see
// IdMapping.h)
class LoadOrder
{
public:
  LoadOrder() = default;

  // All files are regular plugins
  LoadOrder(std::initializer_list<std::string> fileNames);
  explicit LoadOrder(std::vector<std::string> fileNames);

  LoadOrder(std::vector<std::string> fileNames, std::vector<FileSlot> slots);

  operator const std::vector<std::string>&() const noexcept
  {
    return fileNames;
  }
  const std::vector<std::string>& GetFileNames() const noexcept
  {
    return fileNames;
  }

  size_t size() const noexcept { return fileNames.size(); }
  bool empty() const noexcept { return fileNames.empty(); }
  const std::string& operator[](size_t i) const { return fileNames[i]; }
  auto begin() const noexcept { return fileNames.begin(); }
  auto end() const noexcept { return fileNames.end(); }

  // Appends a regular plugin
  void push_back(std::string fileName);

  FileSlot GetSlot(size_t fileIndex) const noexcept;

  // Returns -1 if the file isn't loaded
  int32_t FindFile(std::string_view fileName) const noexcept;

  // Index of the file a combined form id belongs to. Returns -1 for runtime
  // created forms (FF xxxxxx) and ids pointing to files that aren't loaded
  int32_t GetFileIndex(uint32_t formId) const noexcept;

  uint32_t ToFormId(size_t fileIndex, uint32_t localId) const noexcept;

private:
  void IndexSlots();

  std::vector<std::string> fileNames;
  std::vector<FileSlot> slots;

  // File index by slot index, -1 where no file
  std::vector<int32_t> regularFiles;
  std::vector<int32_t> lightFiles;
};

}
