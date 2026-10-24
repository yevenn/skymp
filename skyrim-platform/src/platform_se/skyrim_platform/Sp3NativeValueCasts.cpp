#include "SP3NativeValueCasts.h"

#include "CallNative.h"
#include "NullPointerException.h"
#include "Overloaded.h"
#include "PapyrusTESModPlatform.h"
#include <cstdint>
#include <limits>
#include <optional>
#include <spdlog/spdlog.h>
#include <vector>

#include "RollingContainer.h"

struct SP3NativeValueCasts::Impl
{
  std::optional<uint64_t> numPapyrusUpdates;
  Viet::RollingContainer<CallNative::ObjectPtr> objectPool;
};

SP3NativeValueCasts& SP3NativeValueCasts::GetSingleton()
{
  static SP3NativeValueCasts g_instance;
  return g_instance;
}

CallNative::ObjectPtr SP3NativeValueCasts::JsObjectToNativeObject(
  const Napi::Value& v)
{
  if (v.IsNull() || v.IsUndefined()) {
    return nullptr;
  }

  if (!v.IsObject()) {
    throw std::runtime_error(
      "JsObjectToNativeObject expected object or null or undefined");
  }

  // This might treat NaN as 0. Check omitted for performance.
  const int64_t indexInPool =
    v.As<Napi::Object>()
      .Get(SP3NativeValueCasts::kSkyrimPlatformIndexInPoolProperty)
      .ToNumber()
      .Int64Value();

  if (indexInPool < 0) {
    throw std::runtime_error(fmt::format(
      "JsObjectToNativeObject expected a non-negative {}, but got {}",
      SP3NativeValueCasts::kSkyrimPlatformIndexInPoolProperty, indexInPool));
  }

  const uint64_t unsignedIndexinPool = static_cast<uint64_t>(indexInPool);
  const uint64_t rangeBegin = pImpl->objectPool.GetActiveWindowStart();
  const uint64_t rangeEnd = pImpl->objectPool.GetTotalProcessedCount();
  if (unsignedIndexinPool < rangeBegin || unsignedIndexinPool >= rangeEnd) {
    throw std::runtime_error(
      fmt::format("SP3NativeValueCasts::JsObjectToNativeObject - Invalid {}: "
                  "{}, expected to be in [{}, {}) range",
                  SP3NativeValueCasts::kSkyrimPlatformIndexInPoolProperty,
                  unsignedIndexinPool, rangeBegin, rangeEnd));
  }

  return pImpl->objectPool[unsignedIndexinPool];
}

Napi::Value SP3NativeValueCasts::NativeObjectToJsObject(
  Napi::Env env, const CallNative::ObjectPtr& obj)
{
  if (!obj) {
    return env.Null();
  }

  const auto numPapyrusUpdates = TESModPlatform::GetNumPapyrusUpdates();

  if (pImpl->numPapyrusUpdates != numPapyrusUpdates) {
    pImpl->numPapyrusUpdates = numPapyrusUpdates;
    pImpl->objectPool.ForgetAll();
  }

  const size_t relativeIndex = pImpl->objectPool.InsertBack(obj);
  const uint64_t resultIndex =
    pImpl->objectPool.GetActiveWindowStart() + relativeIndex;
  auto result = Napi::Object::New(env);
  result.Set(SP3NativeValueCasts::kSkyrimPlatformIndexInPoolProperty,
             Napi::Number::New(env, resultIndex));
  return result;
}

CallNative::AnySafe SP3NativeValueCasts::JsArrayToNativeValue(
  const Napi::Array& arr)
{
  const uint32_t length = arr.Length();
  if (length == 0) {
    // Element type unknown: CallNative creates an empty array of the
    // parameter's type
    return std::vector<std::string>();
  }

  const Napi::Value first = arr.Get(0u);
  auto mismatch = [](uint32_t i) {
    return std::runtime_error(
      "Array arguments must hold one type: strings, numbers or booleans "
      "(element " +
      std::to_string(i) + " differs from the first)");
  };

  if (first.IsString()) {
    std::vector<std::string> out;
    out.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
      Napi::Value el = arr.Get(i);
      if (!el.IsString()) {
        throw mismatch(i);
      }
      out.push_back(static_cast<std::string>(el.As<Napi::String>()));
    }
    return out;
  }

  if (first.IsNumber()) {
    std::vector<double> out;
    out.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
      Napi::Value el = arr.Get(i);
      if (!el.IsNumber()) {
        throw mismatch(i);
      }
      out.push_back(el.As<Napi::Number>().DoubleValue());
    }
    return out;
  }

  if (first.IsBoolean()) {
    std::vector<bool> out;
    out.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
      Napi::Value el = arr.Get(i);
      if (!el.IsBoolean()) {
        throw mismatch(i);
      }
      out.push_back(static_cast<bool>(el.As<Napi::Boolean>()));
    }
    return out;
  }

  throw std::runtime_error(
    "Array arguments must hold strings, numbers or booleans");
}

CallNative::AnySafe SP3NativeValueCasts::JsValueToNativeValue(
  const Napi::Value& v)
{
  // TODO: better switch(v.Type()) for performance
  if (v.IsBoolean()) {
    return static_cast<bool>(v.As<Napi::Boolean>());
  }

  if (v.IsNumber()) {
    return static_cast<double>(v.As<Napi::Number>().DoubleValue());
  }

  if (v.IsString()) {
    return static_cast<std::string>(v.As<Napi::String>());
  }

  // Arrays of strings, numbers or booleans, for Papyrus array parameters
  // (e.g. UI.InvokeStringA); CallNative turns them into the parameter's type
  if (v.IsArray()) {
    return JsArrayToNativeValue(v.As<Napi::Array>());
  }

  if (v.IsUndefined() || v.IsNull() || v.IsObject()) {
    return JsObjectToNativeObject(v);
  }

  throw std::runtime_error("Unsupported JavaScript type (" +
                           std::to_string((int)v.Type()) + ")");
}

Napi::Value SP3NativeValueCasts::NativeValueToJsValue(
  Napi::Env env, const CallNative::AnySafe& v)
{
  if (v.valueless_by_exception()) {
    return env.Null();
  }
  return std::visit(
    Viet::Overloaded{
      [env](double v) -> Napi::Value { return Napi::Number::New(env, v); },
      [env](bool v) -> Napi::Value { return Napi::Boolean::New(env, v); },
      [env](const std::string& v) -> Napi::Value {
        return Napi::String::New(env, v);
      },
      [env, this](const CallNative::ObjectPtr& v) -> Napi::Value {
        return NativeObjectToJsObject(env, v);
      },
      [env](const std::vector<std::string>& v) -> Napi::Value {
        auto out = Napi::Array::New(env, v.size());
        for (size_t i = 0; i < v.size(); ++i) {
          out.Set(i, Napi::String::New(env, v[i]));
        }
        return out;
      },
      [env](const std::vector<bool>& v) -> Napi::Value {
        auto out = Napi::Array::New(env, v.size());
        for (size_t i = 0; i < v.size(); ++i) {
          out.Set(i, Napi::Boolean::New(env, v[i]));
        }
        return out;
      },
      [env](const std::vector<double>& v) -> Napi::Value {
        auto out = Napi::Array::New(env, v.size());
        for (size_t i = 0; i < v.size(); ++i) {
          out.Set(i, Napi::Number::New(env, v[i]));
        }
        return out;
      },
      [env, this](const std::vector<CallNative::ObjectPtr>& v) -> Napi::Value {
        auto out = Napi::Array::New(env, v.size());
        for (size_t i = 0; i < v.size(); ++i) {
          out.Set(i, NativeObjectToJsObject(env, v[i]));
        }
        return out;
      },
    },
    v);
}

SP3NativeValueCasts::SP3NativeValueCasts()
{
  pImpl = std::make_shared<Impl>();
}
