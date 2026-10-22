#include "libespm/Combiner.h"
#include "libespm/Browser.h"
#include "libespm/Convert.h"
#include "libespm/Records.h"
#include "libespm/Utils.h"
#include "libespm/espm.h"
#include <array>
#include <cctype>
#include <fmt/format.h>
#include <string>

namespace espm {

Combiner::Combiner()
  : pImpl(nullptr)
{
  pImpl = std::make_unique<CombineBrowser::Impl>();
}

void espm::Combiner::AddSource(Browser* src, const char* fileName) noexcept
{
  if (pImpl->numSources >= std::size(pImpl->sources)) {
    ++pImpl->numSources;
    return;
  }
  pImpl->sources[pImpl->numSources++] = { src, fileName, nullptr };
}

namespace {
bool HasEslExtension(const std::string& fileName)
{
  if (fileName.size() < 4) {
    return false;
  }
  std::string ext = fileName.substr(fileName.size() - 4);
  for (auto& c : ext) {
    c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  }
  return ext == ".esl";
}
}

std::unique_ptr<espm::CombineBrowser> Combiner::Combine()
{
  if (pImpl->numSources > std::size(pImpl->sources)) {
    throw CombineError("too many sources");
  }

  // Like the game, number regular and light plugins separately, each in load
  // order. A plugin is light if it has the ESL flag or the .esl extension
  uint16_t numRegular = 0, numLight = 0;
  for (size_t i = 0; i < pImpl->numSources; ++i) {
    auto& src = pImpl->sources[i];
    if (!src.br) {
      throw CombineError("nullptr source with index " + std::to_string(i));
    }

    const auto tes4 = Convert<TES4>(src.br->LookupById(0));
    if (!tes4) {
      throw CombineError(src.fileName + " doesn't have TES4 record");
    }

    if (tes4->IsLight() || HasEslExtension(src.fileName)) {
      if (numLight >= kMaxLightPlugins) {
        throw CombineError("too many light plugins");
      }
      src.slot = { true, numLight++ };
    } else {
      if (numRegular >= kMaxRegularPlugins) {
        throw CombineError("too many regular plugins");
      }
      src.slot = { false, numRegular++ };
    }
  }

  for (size_t i = 0; i < pImpl->numSources; ++i) {
    auto& src = pImpl->sources[i];

    const auto tes4 = Convert<TES4>(src.br->LookupById(0));
    espm::CompressedFieldsCache dummyCache;
    const auto masters = tes4->GetData(dummyCache).masters;

    auto toComb = std::make_unique<IdMapping>(IdMapping::Direction::kToCombined);
    auto toRaw = std::make_unique<IdMapping>(IdMapping::Direction::kToRaw);
    size_t m = 0;
    for (m = 0; m < masters.size(); ++m) {
      const int globalIdx = pImpl->GetFileIndex(masters[m]);
      if (globalIdx == -1) {
        throw CombineError(src.fileName + " has unresolved dependency (" +
                           masters[m] + ")");
      }
      const auto masterSlot = pImpl->sources[globalIdx].slot;
      toComb->Add(static_cast<uint8_t>(m), masterSlot);
      toRaw->Add(static_cast<uint8_t>(m), masterSlot);
    }
    toComb->Add(static_cast<uint8_t>(m), src.slot);
    toRaw->Add(static_cast<uint8_t>(m), src.slot);
    src.toComb = std::move(toComb);
    src.toRaw = std::move(toRaw);
  }

  std::unique_ptr<espm::CombineBrowser> res(new CombineBrowser);
  res->pImpl = pImpl;
  return res;
}

Combiner::~Combiner() = default;

}
