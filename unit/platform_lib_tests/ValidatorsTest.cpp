#include <catch2/catch_all.hpp>

#include "Validators.h"

TEST_CASE("ValidateDataFilename accepts real plugin names",
          "[Validators]")
{
  REQUIRE(ValidateDataFilename("Skyrim.esm"));
  REQUIRE(ValidateDataFilename("Mage Clothing Expansion.esp"));
  REQUIRE(ValidateDataFilename("JK's Whiterun's Outskirts.esp"));
  REQUIRE(ValidateDataFilename("Cloaks&Capes.esp"));
  REQUIRE(ValidateDataFilename("Sentinel - Master Plugin.esp"));
  REQUIRE(ValidateDataFilename("mage clothing expansion.esp"));
  REQUIRE(ValidateDataFilename("Unofficial (Patch) [v2] +1!.esl"));
}

TEST_CASE("ValidateDataFilename rejects anything leaving Data",
          "[Validators]")
{
  REQUIRE_FALSE(ValidateDataFilename(""));
  REQUIRE_FALSE(ValidateDataFilename("."));
  REQUIRE_FALSE(ValidateDataFilename(".."));
  REQUIRE_FALSE(ValidateDataFilename("../SkyrimSE.exe"));
  REQUIRE_FALSE(ValidateDataFilename("..\\SkyrimSE.exe"));
  REQUIRE_FALSE(ValidateDataFilename("Scripts/x.pex"));
  REQUIRE_FALSE(ValidateDataFilename("C:Windows"));
  REQUIRE_FALSE(ValidateDataFilename("*.esp"));
  REQUIRE_FALSE(ValidateDataFilename("a?.esp"));
  REQUIRE_FALSE(ValidateDataFilename("a\"b.esp"));
  REQUIRE_FALSE(ValidateDataFilename("a|b.esp"));
  REQUIRE_FALSE(ValidateDataFilename(std::string_view("a\0b.esp", 7)));
  REQUIRE_FALSE(ValidateDataFilename("a\nb.esp"));
}
