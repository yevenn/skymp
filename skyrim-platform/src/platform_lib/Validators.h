#pragma once

#include <string_view>

bool ValidateFilename(std::string_view filename, bool allowDots);

// Accepts real plugin/archive names in the Data folder, which often contain
// spaces, apostrophes or '&' ("JK's Whiterun.esp", "Cloaks&Capes.esp"), but
// nothing that could leave that folder: no path separators, drive letters,
// wildcards, control characters, "." or ".."
bool ValidateDataFilename(std::string_view filename);

bool ValidateRelativePath(std::string_view path);
