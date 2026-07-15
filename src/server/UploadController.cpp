#include "UploadController.h"

#include "utils/DesktopFolders.h"
#include "utils/FileName.h"
#include "utils/Log.h"
#include "utils/Utf.h"

#include <exception>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <unordered_map>
#include <utility>

namespace {

bool IsSupportedUploadPath(std::string_view path) {
    return path == "/dropme/upload" || path == "/wifidrop/upload";
}

std::string JsonEscape(const std::string &value) {
    std::string escaped;
    escaped.reserve(value.size());
    for (const unsigned char symbol : value) {
        switch (symbol) {
            case '"':
                escaped += "\\\"";
                break;
            case '\\':
                escaped += "\\\\";
                break;
            case '\b':
                escaped += "\\b";
                break;
            case '\f':
                escaped += "\\f";
                break;
            case '\n':
                escaped += "\\n";
                break;
            case '\r':
                escaped += "\\r";
                break;
            case '\t':
                escaped += "\\t";
                break;
            default:
                if (symbol < 0x20) {
                    escaped += '?';
                } else {
                    escaped.push_back(static_cast<char>(symbol));
                }
                break;
        }
    }
    return escaped;
}

std::unordered_map<std::string, std::string> ParseQuery(const std::string &query) {
    std::unordered_map<std::string, std::string> result;
    std::size_t offset = 0;
    while (offset <= query.size()) {
        const auto separator = query.find('&', offset);
        const auto part = query.substr(offset, separator == std::string::npos ? std::string::npos : separator - offset);
        const auto equals = part.find('=');
        if (equals != std::string::npos) {
            result.emplace(part.substr(0, equals), part.substr(equals + 1));
        } else if (!part.empty()) {
            result.emplace(part, "");
        }

        if (separator == std::string::npos) {
            break;
        }
        offset = separator + 1;
    }
    return result;
}

HttpResponse MakeJsonResponse(int statusCode, std::string reasonPhrase, std::string body) {
    HttpResponse response;
    response.statusCode = statusCode;
    response.reasonPhrase = std::move(reasonPhrase);
    response.headers.emplace("Content-Type", "application/json; charset=utf-8");
    response.body = std::move(body);
    return response;
}

}  // namespace

bool UploadController::HandleRequest(const HttpRequest &request, HttpResponse &response) const {
    if (request.method != "PUT" || !IsSupportedUploadPath(request.path)) {
        return false;
    }

    if (request.headers.find("content-length") == request.headers.end()) {
        Log::Warn("Upload request without Content-Length");
    }

    const auto query = ParseQuery(request.query);
    const auto iterator = query.find("name");
    if (iterator == query.end() || iterator->second.empty()) {
        response = MakeJsonResponse(400, "Bad Request", R"({"ok":false,"error":"Missing file name"})");
        return true;
    }

    const auto decodedName = FileName::UrlDecode(iterator->second);
    const auto validationResult = FileName::ValidateUploadFileName(decodedName);
    if (!validationResult.ok) {
        response = MakeJsonResponse(400,
                                    "Bad Request",
                                    "{\"ok\":false,\"error\":\"" + JsonEscape(validationResult.error) + "\"}");
        return true;
    }

    try {
        const std::filesystem::path folder = DesktopFolders::EnsureIncomingFolder();
        const std::filesystem::path filePath = FileName::MakeUniquePath(folder, validationResult.sanitizedName);

        std::ofstream output(filePath, std::ios::binary);
        if (!output) {
            Log::Error("Failed to open upload destination: " + Utf::WideToUtf8(filePath.native()));
            response = MakeJsonResponse(500, "Internal Server Error", R"({"ok":false,"error":"Failed to save file"})");
            return true;
        }

        output.write(request.body.data(), static_cast<std::streamsize>(request.body.size()));
        if (!output.good()) {
            Log::Error("Failed to write upload destination: " + Utf::WideToUtf8(filePath.native()));
            response = MakeJsonResponse(500, "Internal Server Error", R"({"ok":false,"error":"Failed to save file"})");
            return true;
        }

        const std::string savedPath = Utf::WideToUtf8(filePath.native());
        Log::Info("Upload saved: " + savedPath);
        response = MakeJsonResponse(200,
                                    "OK",
                                    "{\"ok\":true,\"savedPath\":\"" + JsonEscape(savedPath) + "\"}");
        return true;
    } catch (const std::exception &exception) {
        Log::Error(std::string("Upload handling failed: ") + exception.what());
        response = MakeJsonResponse(500, "Internal Server Error", R"({"ok":false,"error":"Failed to save file"})");
        return true;
    }
}
