#include "FormDesc.h"
#include "libespm/Loader.h"
#include <catch2/catch_all.hpp>
#include <cstring>

// Light plugins (ESL) share the FE prefix and are told apart by a 12-bit
// index: FE xxx yyy. These tests build tiny plugins in memory so they don't
// depend on Skyrim's files.

namespace {

class PluginBuilder
{
public:
  PluginBuilder(bool isLight, std::vector<std::string> masters)
  {
    std::vector<char> fields;
    AppendField(fields, "HEDR", std::string(12, '\0'));
    for (auto& master : masters) {
      AppendField(fields, "MAST", master + '\0');
      AppendField(fields, "DATA", std::string(8, '\0'));
    }
    AppendRecord("TES4", isLight ? 0x200 : 0, 0, fields);
  }

  // A record with no fields, enough for lookups by id
  PluginBuilder& AddRecord(const char* type, uint32_t rawId)
  {
    AppendRecord(type, 0, rawId, {});
    return *this;
  }

  const std::vector<char>& GetBuffer() const noexcept { return buf; }

private:
  template <class T>
  static void Append(std::vector<char>& out, T value)
  {
    const char* p = reinterpret_cast<const char*>(&value);
    out.insert(out.end(), p, p + sizeof(T));
  }

  static void AppendField(std::vector<char>& out, const char* type,
                          const std::string& data)
  {
    out.insert(out.end(), type, type + 4);
    Append<uint16_t>(out, static_cast<uint16_t>(data.size()));
    out.insert(out.end(), data.begin(), data.end());
  }

  void AppendRecord(const char* type, uint32_t flags, uint32_t id,
                    const std::vector<char>& data)
  {
    buf.insert(buf.end(), type, type + 4);
    Append<uint32_t>(buf, static_cast<uint32_t>(data.size()));
    Append<uint32_t>(buf, flags);
    Append<uint32_t>(buf, id);
    Append<uint32_t>(buf, 0);      // revision
    Append<uint16_t>(buf, 44);     // version
    Append<uint16_t>(buf, 0);      // unknown
    buf.insert(buf.end(), data.begin(), data.end());
  }

  std::vector<char> buf;
};

// Load order: Skyrim.esm (regular 00), Light.esp (ESL flag, light 000),
// Mod.esp (regular 01, masters Skyrim.esm and Light.esp),
// Extension.esl (light by extension, light 001)
struct TestLoadOrder
{
  TestLoadOrder()
    : skyrim(false, {})
    , light(true, { "Skyrim.esm" })
    , mod(false, { "Skyrim.esm", "Light.esp" })
    , extension(false, { "Skyrim.esm" })
  {
    skyrim.AddRecord("MISC", 0x00000800);
    light.AddRecord("MISC", 0x01000801).AddRecord("MISC", 0x00000800);
    mod.AddRecord("MISC", 0x02000900).AddRecord("MISC", 0x01000801);
    extension.AddRecord("MISC", 0x01000802);

    for (auto plugin : { &skyrim, &light, &mod, &extension }) {
      const auto& b = plugin->GetBuffer();
      browsers.push_back(std::make_unique<espm::Browser>(b.data(), b.size()));
    }

    espm::Combiner combiner;
    const char* names[] = { "Skyrim.esm", "Light.esp", "Mod.esp",
                            "Extension.esl" };
    for (size_t i = 0; i < browsers.size(); ++i) {
      combiner.AddSource(browsers[i].get(), names[i]);
    }
    browser = combiner.Combine();
  }

  PluginBuilder skyrim, light, mod, extension;
  std::vector<std::unique_ptr<espm::Browser>> browsers;
  std::unique_ptr<espm::CombineBrowser> browser;
};

}

TEST_CASE("Combined ids for regular and light plugins", "[espm][light]")
{
  using espm::FileSlot;

  REQUIRE(espm::MakeCombinedId(FileSlot{ false, 0x05 }, 0x123456) ==
          0x05123456);
  REQUIRE(espm::MakeCombinedId(FileSlot{ true, 0x003 }, 0x812) == 0xFE003812);
  // Light plugins only have 12 bits for local ids
  REQUIRE(espm::MakeCombinedId(FileSlot{ true, 0x003 }, 0x01000812) ==
          0xFE003812);

  FileSlot slot;
  uint32_t localId = 0;
  REQUIRE(espm::SplitCombinedId(0xFE003812, &slot, &localId));
  REQUIRE(slot == FileSlot{ true, 0x003 });
  REQUIRE(localId == 0x812);

  REQUIRE(espm::SplitCombinedId(0x05123456, &slot, &localId));
  REQUIRE(slot == FileSlot{ false, 0x05 });
  REQUIRE(localId == 0x123456);

  // Runtime-created forms don't belong to any plugin
  REQUIRE_FALSE(espm::SplitCombinedId(0xFF000001, &slot, &localId));
}

TEST_CASE("Combiner assigns light plugins FE slots", "[espm][light]")
{
  TestLoadOrder t;
  auto& br = *t.browser;

  REQUIRE(br.GetFileSlot(0) == espm::FileSlot{ false, 0 });
  REQUIRE(br.GetFileSlot(1) == espm::FileSlot{ true, 0 });
  // Light plugins don't take a regular slot, so Mod.esp is 01, not 02
  REQUIRE(br.GetFileSlot(2) == espm::FileSlot{ false, 1 });
  REQUIRE(br.GetFileSlot(3) == espm::FileSlot{ true, 1 });
}

TEST_CASE("Records from light plugins are found by FE ids", "[espm][light]")
{
  TestLoadOrder t;
  auto& br = *t.browser;

  auto lightRecord = br.LookupById(0xFE000801);
  REQUIRE(lightRecord.rec);
  // The override in Mod.esp wins, as it's later in the load order
  REQUIRE(lightRecord.fileIdx == 2);
  REQUIRE(lightRecord.ToGlobalId(lightRecord.rec->GetId()) == 0xFE000801);

  auto all = br.LookupByIdAll(0xFE000801);
  REQUIRE(all.size() == 2);
  REQUIRE(all[0].fileIdx == 1);
  REQUIRE(all[1].fileIdx == 2);

  auto extensionRecord = br.LookupById(0xFE001802);
  REQUIRE(extensionRecord.rec);
  REQUIRE(extensionRecord.fileIdx == 3);

  auto modRecord = br.LookupById(0x01000900);
  REQUIRE(modRecord.rec);
  REQUIRE(modRecord.fileIdx == 2);
  REQUIRE(modRecord.ToGlobalId(modRecord.rec->GetId()) == 0x01000900);

  // A light plugin overriding a regular master keeps the master's id
  REQUIRE(br.LookupByIdAll(0x00000800).size() == 2);

  // Ids that would belong to plugins if light ones took regular slots
  REQUIRE_FALSE(br.LookupById(0x02000900).rec);
  REQUIRE_FALSE(br.LookupById(0x01000801).rec);
  REQUIRE_FALSE(br.LookupById(0x03000802).rec);
}

TEST_CASE("Id mappings convert light masters both ways", "[espm][light]")
{
  TestLoadOrder t;
  auto& br = *t.browser;

  // Mod.esp: masters Skyrim.esm (raw 00), Light.esp (raw 01), itself (raw 02)
  auto& toComb = *br.GetCombMapping(2);
  auto& toRaw = *br.GetRawMapping(2);

  REQUIRE(espm::utils::GetMappedId(0x00000800, toComb) == 0x00000800);
  REQUIRE(espm::utils::GetMappedId(0x01000801, toComb) == 0xFE000801);
  REQUIRE(espm::utils::GetMappedId(0x02000900, toComb) == 0x01000900);

  REQUIRE(espm::utils::GetMappedId(0x00000800, toRaw) == 0x00000800);
  REQUIRE(espm::utils::GetMappedId(0xFE000801, toRaw) == 0x01000801);
  REQUIRE(espm::utils::GetMappedId(0x01000900, toRaw) == 0x02000900);

  // Extension.esl isn't a master of Mod.esp, runtime forms never map
  REQUIRE(espm::utils::GetMappedId(0xFE001802, toRaw) >= 0xff000000);
  REQUIRE(espm::utils::GetMappedId(0xFF000001, toRaw) >= 0xff000000);
}

TEST_CASE("FormDesc round-trips light plugin ids", "[FormDesc][light]")
{
  espm::LoadOrder files(
    { "Skyrim.esm", "Light.esp", "Mod.esp", "Extension.esl" },
    { { false, 0 }, { true, 0 }, { false, 1 }, { true, 1 } });

  REQUIRE(FormDesc::FromFormId(0xFE001802, files) ==
          FormDesc(0x802, "Extension.esl"));
  REQUIRE(FormDesc::FromFormId(0xFE000801, files) ==
          FormDesc(0x801, "Light.esp"));
  REQUIRE(FormDesc::FromFormId(0x01000900, files) ==
          FormDesc(0x900, "Mod.esp"));
  REQUIRE(FormDesc::FromFormId(0xff000bbb, files) == FormDesc(0xbbb, ""));

  REQUIRE(FormDesc(0x802, "Extension.esl").ToFormId(files) == 0xFE001802);
  REQUIRE(FormDesc(0x801, "Light.esp").ToFormId(files) == 0xFE000801);
  REQUIRE(FormDesc(0x900, "Mod.esp").ToFormId(files) == 0x01000900);

  REQUIRE(files.GetFileIndex(0xFE001802) == 3);
  REQUIRE(files.GetFileIndex(0x01000900) == 2);
  REQUIRE(files.GetFileIndex(0xFE002000) == -1);
  REQUIRE(files.GetFileIndex(0x02000000) == -1);
  REQUIRE(files.GetFileIndex(0xFF000001) == -1);

  REQUIRE_THROWS(FormDesc::FromFormId(0xFE005000, files));
  REQUIRE_THROWS(FormDesc(0x1, "Missing.esl").ToFormId(files));
}
