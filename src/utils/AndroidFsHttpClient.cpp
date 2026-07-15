#include "AndroidFsHttpClient.h"

#include "Utf.h"

#include <windows.h>
#include <winhttp.h>

#include <algorithm>
#include <charconv>
#include <cstdio>
#include <cwchar>
#include <limits>
#include <optional>
#include <sstream>
#include <string_view>
#include <utility>
#include <sys/stat.h>

namespace {

class WinHttpHandle {
public:
    explicit WinHttpHandle(HINTERNET handle = nullptr) : handle_(handle) {
    }

    ~WinHttpHandle() {
        if (handle_ != nullptr) {
            WinHttpCloseHandle(handle_);
        }
    }

    WinHttpHandle(const WinHttpHandle &) = delete;
    WinHttpHandle &operator=(const WinHttpHandle &) = delete;

    WinHttpHandle(WinHttpHandle &&other) noexcept : handle_(other.handle_) {
        other.handle_ = nullptr;
    }

    WinHttpHandle &operator=(WinHttpHandle &&other) noexcept {
        if (this != &other) {
            if (handle_ != nullptr) {
                WinHttpCloseHandle(handle_);
            }
            handle_ = other.handle_;
            other.handle_ = nullptr;
        }
        return *this;
    }

    HINTERNET get() const {
        return handle_;
    }

private:
    HINTERNET handle_{nullptr};
};

std::optional<std::pair<std::string_view, std::string_view>> SplitLine(std::string_view line) {
    const auto separator = line.find('=');
    if (separator == std::string_view::npos) {
        return std::nullopt;
    }
    return std::make_pair(line.substr(0, separator), line.substr(separator + 1));
}

bool QueryStatusCode(HINTERNET request, std::uint32_t &statusCode, std::string *errorMessage) {
    DWORD rawStatusCode = 0;
    DWORD size = sizeof(rawStatusCode);
    if (!WinHttpQueryHeaders(request,
                             WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                             WINHTTP_HEADER_NAME_BY_INDEX,
                             &rawStatusCode,
                             &size,
                             WINHTTP_NO_HEADER_INDEX)) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpQueryHeaders failed with error " + std::to_string(GetLastError());
        }
        return false;
    }
    statusCode = rawStatusCode;
    return true;
}

bool ReadResponseBody(HINTERNET request, std::vector<char> &body, std::string *errorMessage) {
    body.clear();
    for (;;) {
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request, &available)) {
            if (errorMessage != nullptr) {
                *errorMessage = "WinHttpQueryDataAvailable failed with error " + std::to_string(GetLastError());
            }
            return false;
        }
        if (available == 0) {
            return true;
        }

        const auto offset = body.size();
        body.resize(offset + available);
        DWORD read = 0;
        if (!WinHttpReadData(request, body.data() + offset, available, &read)) {
            if (errorMessage != nullptr) {
                *errorMessage = "WinHttpReadData failed with error " + std::to_string(GetLastError());
            }
            return false;
        }
        body.resize(offset + read);
    }
}

}  // namespace

AndroidFsHttpClient::AndroidFsHttpClient(std::string host, int port)
    : host_(Utf::Utf8ToWide(std::move(host))),
      port_(port) {
}

bool AndroidFsHttpClient::GetMetadata(const std::string &path, NodeMetadata &metadata, std::string *errorMessage) const {
    HttpResponse response;
    if (!SendRequest(L"GET", BuildMetaPath(path), {}, response, errorMessage)) {
        return false;
    }
    if (response.statusCode == 404) {
        metadata = NodeMetadata{};
        return true;
    }
    if (response.statusCode != 200) {
        if (errorMessage != nullptr) {
            *errorMessage = "Metadata request failed with HTTP " + std::to_string(response.statusCode);
        }
        return false;
    }

    metadata = NodeMetadata{};
    metadata.exists = true;
    return ParseMetadataBody(NarrowBody(response.body), metadata);
}

bool AndroidFsHttpClient::ListDirectory(const std::string &path,
                                        std::vector<DirectoryEntry> &entries,
                                        std::string *errorMessage) const {
    HttpResponse response;
    if (!SendRequest(L"GET", BuildListPath(path), {}, response, errorMessage)) {
        return false;
    }
    if (response.statusCode != 200) {
        if (errorMessage != nullptr) {
            *errorMessage = "Directory list request failed with HTTP " + std::to_string(response.statusCode);
        }
        return false;
    }
    return ParseListBody(NarrowBody(response.body), entries);
}

bool AndroidFsHttpClient::ReadFileRange(const std::string &path,
                                        std::uint64_t offset,
                                        std::uint32_t size,
                                        std::vector<char> &bytes,
                                        std::string *errorMessage) const {
    if (size == 0) {
        bytes.clear();
        return true;
    }

    const std::uint64_t endInclusive = offset + static_cast<std::uint64_t>(size) - 1;
    const std::wstring rangeHeader = L"Range: bytes=" + std::to_wstring(offset) + L"-" + std::to_wstring(endInclusive);

    HttpResponse response;
    if (!SendRequest(L"GET", BuildFilePath(path), {rangeHeader}, response, errorMessage)) {
        return false;
    }
    if (response.statusCode == 416) {
        bytes.clear();
        return true;
    }
    if (response.statusCode != 200 && response.statusCode != 206) {
        if (errorMessage != nullptr) {
            *errorMessage = "Read request failed with HTTP " + std::to_string(response.statusCode);
        }
        return false;
    }

    bytes = std::move(response.body);
    return true;
}

bool AndroidFsHttpClient::UploadFile(const std::string &path,
                                     const std::wstring &localFilePath,
                                     std::string *errorMessage) const {
    HttpResponse response;
    if (!SendFileRequest(L"PUT",
                         BuildFilePath(path),
                         {L"Content-Type: application/octet-stream"},
                         localFilePath,
                         response,
                         errorMessage)) {
        return false;
    }
    if (response.statusCode != 200 && response.statusCode != 201 && response.statusCode != 204) {
        if (errorMessage != nullptr) {
            *errorMessage = "Upload request failed with HTTP " + std::to_string(response.statusCode);
        }
        return false;
    }
    return true;
}

bool AndroidFsHttpClient::DeletePath(const std::string &path, std::string *errorMessage) const {
    HttpResponse response;
    if (!SendRequest(L"DELETE", BuildFilePath(path), {}, response, errorMessage)) {
        return false;
    }
    if (response.statusCode != 200 && response.statusCode != 204) {
        if (errorMessage != nullptr) {
            *errorMessage = "Delete request failed with HTTP " + std::to_string(response.statusCode);
        }
        return false;
    }
    return true;
}

bool AndroidFsHttpClient::CreateRemoteDirectory(const std::string &path, std::string *errorMessage) const {
    HttpResponse response;
    if (!SendRequest(L"MKCOL", BuildFilePath(path), {}, response, errorMessage)) {
        return false;
    }
    if (response.statusCode != 201) {
        if (errorMessage != nullptr) {
            *errorMessage = "Create directory request failed with HTTP " + std::to_string(response.statusCode);
        }
        return false;
    }
    return true;
}

bool AndroidFsHttpClient::MovePath(const std::string &sourcePath,
                                   const std::string &destinationPath,
                                   bool overwrite,
                                   std::string *errorMessage) const {
    HttpResponse response;
    const std::wstring destinationHeader = L"Destination: " + BuildDestinationHeaderValue(destinationPath);
    const std::wstring overwriteHeader = std::wstring(L"Overwrite: ") + (overwrite ? L"T" : L"F");
    if (!SendRequest(L"MOVE",
                     BuildFilePath(sourcePath),
                     {destinationHeader, overwriteHeader},
                     response,
                     errorMessage)) {
        return false;
    }
    if (response.statusCode != 201 && response.statusCode != 204) {
        if (errorMessage != nullptr) {
            *errorMessage = "Move request failed with HTTP " + std::to_string(response.statusCode);
        }
        return false;
    }
    return true;
}

bool AndroidFsHttpClient::SendRequest(const std::wstring &method,
                                      const std::wstring &pathAndQuery,
                                      const std::vector<std::wstring> &headers,
                                      HttpResponse &response,
                                      std::string *errorMessage) const {
    WinHttpHandle session(WinHttpOpen(L"DROPME-WinFsp/1.0",
                                      WINHTTP_ACCESS_TYPE_NO_PROXY,
                                      WINHTTP_NO_PROXY_NAME,
                                      WINHTTP_NO_PROXY_BYPASS,
                                      0));
    if (session.get() == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpOpen failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    WinHttpHandle connection(WinHttpConnect(session.get(), host_.c_str(), static_cast<INTERNET_PORT>(port_), 0));
    if (connection.get() == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpConnect failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    WinHttpHandle request(WinHttpOpenRequest(connection.get(),
                                             method.c_str(),
                                             pathAndQuery.c_str(),
                                             nullptr,
                                             WINHTTP_NO_REFERER,
                                             WINHTTP_DEFAULT_ACCEPT_TYPES,
                                             0));
    if (request.get() == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpOpenRequest failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    for (const auto &header : headers) {
        if (!WinHttpAddRequestHeaders(request.get(), header.c_str(), -1L, WINHTTP_ADDREQ_FLAG_ADD)) {
            if (errorMessage != nullptr) {
                *errorMessage = "WinHttpAddRequestHeaders failed with error " + std::to_string(GetLastError());
            }
            return false;
        }
    }

    if (!WinHttpSendRequest(request.get(),
                            WINHTTP_NO_ADDITIONAL_HEADERS,
                            0,
                            WINHTTP_NO_REQUEST_DATA,
                            0,
                            0,
                            0)) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpSendRequest failed with error " + std::to_string(GetLastError());
        }
        return false;
    }
    if (!WinHttpReceiveResponse(request.get(), nullptr)) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpReceiveResponse failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    return QueryStatusCode(request.get(), response.statusCode, errorMessage) &&
        ReadResponseBody(request.get(), response.body, errorMessage);
}

bool AndroidFsHttpClient::SendFileRequest(const std::wstring &method,
                                          const std::wstring &pathAndQuery,
                                          const std::vector<std::wstring> &headers,
                                          const std::wstring &localFilePath,
                                          HttpResponse &response,
                                          std::string *errorMessage) const {
    struct _stat64 fileInfo{};
    if (_wstat64(localFilePath.c_str(), &fileInfo) != 0) {
        if (errorMessage != nullptr) {
            *errorMessage = "Could not stat temporary file for upload";
        }
        return false;
    }
    const auto fileSize = static_cast<std::uint64_t>(fileInfo.st_size);
    if (fileSize > std::numeric_limits<DWORD>::max()) {
        if (errorMessage != nullptr) {
            *errorMessage = "Temporary file is larger than the supported upload limit";
        }
        return false;
    }

    std::FILE *stream = _wfopen(localFilePath.c_str(), L"rb");
    if (stream == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "Could not open temporary file for upload";
        }
        return false;
    }

    WinHttpHandle session(WinHttpOpen(L"DROPME-WinFsp/1.0",
                                      WINHTTP_ACCESS_TYPE_NO_PROXY,
                                      WINHTTP_NO_PROXY_NAME,
                                      WINHTTP_NO_PROXY_BYPASS,
                                      0));
    if (session.get() == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpOpen failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    WinHttpHandle connection(WinHttpConnect(session.get(), host_.c_str(), static_cast<INTERNET_PORT>(port_), 0));
    if (connection.get() == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpConnect failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    WinHttpHandle request(WinHttpOpenRequest(connection.get(),
                                             method.c_str(),
                                             pathAndQuery.c_str(),
                                             nullptr,
                                             WINHTTP_NO_REFERER,
                                             WINHTTP_DEFAULT_ACCEPT_TYPES,
                                             0));
    if (request.get() == nullptr) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpOpenRequest failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    for (const auto &header : headers) {
        if (!WinHttpAddRequestHeaders(request.get(), header.c_str(), -1L, WINHTTP_ADDREQ_FLAG_ADD)) {
            if (errorMessage != nullptr) {
                *errorMessage = "WinHttpAddRequestHeaders failed with error " + std::to_string(GetLastError());
            }
            return false;
        }
    }

    const DWORD totalLength = static_cast<DWORD>(fileSize);
    if (!WinHttpSendRequest(request.get(),
                            WINHTTP_NO_ADDITIONAL_HEADERS,
                            0,
                            WINHTTP_NO_REQUEST_DATA,
                            0,
                            totalLength,
                            0)) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpSendRequest failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    std::vector<char> buffer(64 * 1024);
    while (true) {
        const std::size_t readCount = fread(buffer.data(), 1, buffer.size(), stream);
        if (readCount == 0) {
            break;
        }
        DWORD written = 0;
        if (!WinHttpWriteData(request.get(), buffer.data(), static_cast<DWORD>(readCount), &written)) {
            fclose(stream);
            if (errorMessage != nullptr) {
                *errorMessage = "WinHttpWriteData failed with error " + std::to_string(GetLastError());
            }
            return false;
        }
        if (written != static_cast<DWORD>(readCount)) {
            fclose(stream);
            if (errorMessage != nullptr) {
                *errorMessage = "WinHttpWriteData wrote fewer bytes than expected";
            }
            return false;
        }
    }
    if (ferror(stream) != 0) {
        fclose(stream);
        if (errorMessage != nullptr) {
            *errorMessage = "Reading temporary file for upload failed";
        }
        return false;
    }
    fclose(stream);

    if (!WinHttpReceiveResponse(request.get(), nullptr)) {
        if (errorMessage != nullptr) {
            *errorMessage = "WinHttpReceiveResponse failed with error " + std::to_string(GetLastError());
        }
        return false;
    }

    return QueryStatusCode(request.get(), response.statusCode, errorMessage) &&
        ReadResponseBody(request.get(), response.body, errorMessage);
}

std::wstring AndroidFsHttpClient::BuildMetaPath(const std::string &path) {
    return L"/.wifidropfs/meta?path=" + UrlEncode(path);
}

std::wstring AndroidFsHttpClient::BuildListPath(const std::string &path) {
    return L"/.wifidropfs/list?path=" + UrlEncode(path);
}

std::wstring AndroidFsHttpClient::BuildFilePath(const std::string &path) {
    return UrlEncodePath(path);
}

std::wstring AndroidFsHttpClient::BuildDestinationHeaderValue(const std::string &path) const {
    return L"http://" + host_ + L":" + std::to_wstring(port_) + BuildFilePath(path);
}

std::wstring AndroidFsHttpClient::UrlEncode(const std::string &value) {
    static constexpr char kHex[] = "0123456789ABCDEF";
    std::wstring encoded;
    for (const unsigned char symbol : value) {
        const bool isUnreserved =
            (symbol >= 'a' && symbol <= 'z') ||
            (symbol >= 'A' && symbol <= 'Z') ||
            (symbol >= '0' && symbol <= '9') ||
            symbol == '-' || symbol == '_' || symbol == '.' || symbol == '~';
        if (isUnreserved) {
            encoded.push_back(static_cast<wchar_t>(symbol));
        } else {
            encoded.push_back(L'%');
            encoded.push_back(static_cast<wchar_t>(kHex[(symbol >> 4) & 0x0F]));
            encoded.push_back(static_cast<wchar_t>(kHex[symbol & 0x0F]));
        }
    }
    return encoded;
}

std::wstring AndroidFsHttpClient::UrlEncodePath(const std::string &value) {
    if (value.empty() || value == "/") {
        return L"/";
    }

    std::wstring encoded = L"/";
    std::size_t segmentStart = 0;
    const std::string normalized = value[0] == '/' ? value.substr(1) : value;
    while (segmentStart <= normalized.size()) {
        const auto separator = normalized.find('/', segmentStart);
        const auto segment = separator == std::string::npos
            ? normalized.substr(segmentStart)
            : normalized.substr(segmentStart, separator - segmentStart);
        if (!segment.empty()) {
            encoded += UrlEncode(segment);
        }
        if (separator == std::string::npos) {
            break;
        }
        encoded.push_back(L'/');
        segmentStart = separator + 1;
    }
    return encoded;
}

std::string AndroidFsHttpClient::UrlDecode(const std::string &value) {
    std::string decoded;
    decoded.reserve(value.size());
    for (std::size_t i = 0; i < value.size(); ++i) {
        if (value[i] == '%' && i + 2 < value.size()) {
            unsigned int byteValue = 0;
            const std::string hex = value.substr(i + 1, 2);
            std::stringstream stream;
            stream << std::hex << hex;
            stream >> byteValue;
            decoded.push_back(static_cast<char>(byteValue));
            i += 2;
        } else if (value[i] == '+') {
            decoded.push_back(' ');
        } else {
            decoded.push_back(value[i]);
        }
    }
    return decoded;
}

std::string AndroidFsHttpClient::NarrowBody(const std::vector<char> &body) {
    return std::string(body.begin(), body.end());
}

bool AndroidFsHttpClient::ParseMetadataBody(const std::string &body, NodeMetadata &metadata) {
    std::istringstream stream(body);
    for (std::string line; std::getline(stream, line);) {
        if (line.empty()) {
            continue;
        }
        const auto split = SplitLine(line);
        if (!split.has_value()) {
            continue;
        }
        const auto [key, value] = *split;
        if (key == "directory") {
            metadata.directory = ParseBool(value);
        } else if (key == "size") {
            metadata.size = ParseUnsigned(value);
        } else if (key == "lastModified") {
            metadata.lastModified = ParseUnsigned(value);
        } else if (key == "writable") {
            metadata.writable = ParseBool(value);
        } else if (key == "name") {
            metadata.name = UrlDecode(std::string(value));
        } else if (key == "etag") {
            metadata.etag = UrlDecode(std::string(value));
        }
    }
    return true;
}

bool AndroidFsHttpClient::ParseListBody(const std::string &body, std::vector<DirectoryEntry> &entries) {
    entries.clear();
    std::istringstream stream(body);
    for (std::string line; std::getline(stream, line);) {
        if (!line.starts_with("entry=")) {
            continue;
        }
        std::string_view payload(line.data() + 6, line.size() - 6);
        const auto firstTab = payload.find('\t');
        const auto secondTab = firstTab == std::string_view::npos ? std::string_view::npos : payload.find('\t', firstTab + 1);
        const auto thirdTab = secondTab == std::string_view::npos ? std::string_view::npos : payload.find('\t', secondTab + 1);
        const auto fourthTab = thirdTab == std::string_view::npos ? std::string_view::npos : payload.find('\t', thirdTab + 1);
        if (fourthTab == std::string_view::npos) {
            continue;
        }

        DirectoryEntry entry;
        entry.name = UrlDecode(std::string(payload.substr(0, firstTab)));
        entry.directory = ParseBool(payload.substr(firstTab + 1, secondTab - firstTab - 1));
        entry.size = ParseUnsigned(payload.substr(secondTab + 1, thirdTab - secondTab - 1));
        entry.lastModified = ParseUnsigned(payload.substr(thirdTab + 1, fourthTab - thirdTab - 1));
        entry.writable = ParseBool(payload.substr(fourthTab + 1));
        entries.push_back(std::move(entry));
    }
    return true;
}

bool AndroidFsHttpClient::ParseBool(std::string_view value) {
    return value == "1" || value == "true";
}

std::uint64_t AndroidFsHttpClient::ParseUnsigned(std::string_view value) {
    std::uint64_t result = 0;
    std::from_chars(value.data(), value.data() + value.size(), result);
    return result;
}
