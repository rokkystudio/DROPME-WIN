#include "ControlServer.h"

#include "Protocol.h"
#include "utils/Log.h"
#include "utils/Utf.h"
#include "utils/WebDavDriveMapper.h"

#include <shellapi.h>
#include <windows.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <chrono>
#include <cctype>
#include <random>
#include <regex>
#include <sstream>
#include <string_view>
#include <thread>
#include <utility>

namespace {

constexpr std::string_view kPrimaryApiBase = "/dropme";
constexpr std::string_view kLegacyApiBase = "/wifidrop";

std::string GetComputerNameUtf8();

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

bool SendAll(SOCKET socketHandle, std::string_view bytes) {
    std::size_t offset = 0;
    while (offset < bytes.size()) {
        const int sent = send(socketHandle,
                              bytes.data() + offset,
                              static_cast<int>(bytes.size() - offset),
                              0);
        if (sent <= 0) {
            return false;
        }
        offset += static_cast<std::size_t>(sent);
    }
    return true;
}

std::string MakeRegexForStringField(const std::string &fieldName) {
    return "\"" + fieldName + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"";
}

std::string MakeRegexForIntField(const std::string &fieldName) {
    return "\"" + fieldName + "\"\\s*:\\s*([0-9]+)";
}

std::string MakeRegexForBoolField(const std::string &fieldName) {
    return "\"" + fieldName + "\"\\s*:\\s*(true|false)";
}

std::string BuildInfoJson() {
    std::ostringstream stream;
    stream << "{\"app\":\"DROPME\",\"role\":\"windows-server\",\"protocolVersion\":"
           << Protocol::kProtocolVersion
           << ",\"deviceName\":\"" << JsonEscape(GetComputerNameUtf8())
           << "\",\"tcpPort\":" << Protocol::kTcpPort
           << ",\"udpPort\":" << Protocol::kDiscoveryPort << "}";
    return stream.str();
}

bool MatchesPath(std::string_view requestPath, std::string_view suffix) {
    return requestPath == (std::string(kPrimaryApiBase) + std::string(suffix)) ||
        requestPath == (std::string(kLegacyApiBase) + std::string(suffix));
}

std::string ExtractSessionClientId(std::string_view path) {
    constexpr std::string_view kSessionSuffix = "/client/session/";
    if (path.starts_with(kPrimaryApiBase)) {
        const std::size_t offset = kPrimaryApiBase.size() + kSessionSuffix.size();
        return path.size() > offset ? std::string(path.substr(offset)) : std::string();
    }
    if (path.starts_with(kLegacyApiBase)) {
        const std::size_t offset = kLegacyApiBase.size() + kSessionSuffix.size();
        return path.size() > offset ? std::string(path.substr(offset)) : std::string();
    }
    return {};
}

std::string GetComputerNameUtf8() {
    wchar_t computerName[MAX_COMPUTERNAME_LENGTH + 1]{};
    constexpr DWORD computerNameCapacity = static_cast<DWORD>(sizeof(computerName) / sizeof(computerName[0]));
    DWORD size = computerNameCapacity;
    if (!GetComputerNameW(computerName, &size)) {
        return "Windows PC";
    }
    return Utf::WideToUtf8(computerName);
}

std::string ExtractJsonString(const std::string &json, const std::string &fieldName) {
    const std::regex pattern(MakeRegexForStringField(fieldName), std::regex::ECMAScript);
    std::smatch match;
    if (!std::regex_search(json, match, pattern) || match.size() < 2) {
        return "";
    }
    return Utf::JsonUnescape(match[1].str());
}

int ExtractJsonInt(const std::string &json, const std::string &fieldName, int defaultValue) {
    const std::regex pattern(MakeRegexForIntField(fieldName), std::regex::ECMAScript);
    std::smatch match;
    if (!std::regex_search(json, match, pattern) || match.size() < 2) {
        return defaultValue;
    }
    return std::stoi(match[1].str());
}

bool ExtractJsonBool(const std::string &json, const std::string &fieldName, bool defaultValue) {
    const std::regex pattern(MakeRegexForBoolField(fieldName), std::regex::ECMAScript);
    std::smatch match;
    if (!std::regex_search(json, match, pattern) || match.size() < 2) {
        return defaultValue;
    }
    return match[1].str() == "true";
}

std::string GenerateClientId() {
    std::random_device randomDevice;
    std::mt19937_64 generator(randomDevice());
    std::uniform_int_distribution<unsigned int> distribution(0, 15);
    std::string value;
    value.reserve(36);
    constexpr int groups[] = {8, 4, 4, 4, 12};
    const int groupsCount = static_cast<int>(sizeof(groups) / sizeof(groups[0]));
    for (int groupIndex = 0; groupIndex < groupsCount; ++groupIndex) {
        if (groupIndex > 0) {
            value.push_back('-');
        }
        for (int i = 0; i < groups[groupIndex]; ++i) {
            const unsigned int digit = distribution(generator);
            value.push_back("0123456789abcdef"[digit]);
        }
    }
    return value;
}

void OpenMountedDriveInExplorer(const AndroidClient &client) {
    if (client.driveLetter.empty()) {
        return;
    }

    const std::wstring drivePath = Utf::Utf8ToWide(client.driveLetter) + L":\\";
    const auto result = reinterpret_cast<INT_PTR>(
        ShellExecuteW(nullptr, L"open", drivePath.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
    if (result <= 32) {
        Log::Warn("Could not auto-open mounted drive " + client.driveLetter + " in Explorer. ShellExecuteW=" +
                  std::to_string(result));
    } else {
        Log::Info("Mounted drive auto-opened in Explorer: " + client.driveLetter);
    }
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

ControlServer::ControlServer(ClientManager &clientManager) : clientManager_(clientManager) {
}

ControlRequestDisposition ControlServer::HandleRequest(const HttpRequest &request, SOCKET clientSocket, HttpResponse &response) {
    if (request.method == "GET" && MatchesPath(request.path, "/info")) {
        response = HandleInfoRequest();
        return ControlRequestDisposition::ResponseReady;
    }

    if (request.method == "POST" && MatchesPath(request.path, "/client/connect")) {
        response = HandleConnectRequest(request);
        return ControlRequestDisposition::ResponseReady;
    }

    if (request.method == "GET" &&
        (request.path.starts_with("/dropme/client/session/") ||
            request.path.starts_with("/wifidrop/client/session/"))) {
        return HandleSessionRequest(request, clientSocket);
    }

    return ControlRequestDisposition::NotHandled;
}

HttpResponse ControlServer::HandleInfoRequest() const {
    Log::Info("HTTP discovery request received");
    return MakeJsonResponse(200, "OK", BuildInfoJson());
}

HttpResponse ControlServer::HandleConnectRequest(const HttpRequest &request) {
    const int protocolVersion = ExtractJsonInt(request.body, "protocolVersion", -1);
    if (protocolVersion != Protocol::kProtocolVersion) {
        return MakeJsonResponse(400, "Bad Request", R"({"accepted":false,"error":"Unsupported protocolVersion"})");
    }

    AndroidClient client;
    client.clientId = GenerateClientId();
    client.deviceNameUtf8 = ExtractJsonString(request.body, "deviceName");
    client.deviceNumberUtf8 = ExtractJsonString(request.body, "deviceNumber");
    client.webDavHost = ExtractJsonString(request.body, "webDavHost");
    client.webDavPort = ExtractJsonInt(request.body, "webDavPort", 0);
    client.webDavBasePath = ExtractJsonString(request.body, "webDavBasePath");
    client.readOnly = ExtractJsonBool(request.body, "readOnly", false);
    client.mountReady = ExtractJsonBool(request.body, "mountReady", false);
    const bool hasWebDavEndpoint = !client.webDavHost.empty() && client.webDavPort > 0;
    if (!hasWebDavEndpoint) {
        client.mountReady = false;
    } else if (!client.mountReady) {
        client.mountReady = true;
    }
    client.remoteIp = request.remoteIp;
    client.driveName = AndroidClient::BuildDriveName(client.deviceNameUtf8, client.deviceNumberUtf8);
    client.sessionState = AndroidSessionState::Pending;
    if (client.mountReady) {
        const auto mountResult = WebDavDriveMapper::Mount(client);
        if (mountResult.driveLetter.has_value()) {
            client.driveLetter = *mountResult.driveLetter;
            client.mountError.clear();
        } else {
            client.driveLetter.clear();
            if (client.mountError.empty()) {
                client.mountError = mountResult.errorMessage;
            }
        }
    } else {
        client.driveLetter.clear();
        if (client.mountError.empty()) {
            client.mountError.clear();
        }
    }

    clientManager_.AddClient(client);
    if (!client.driveLetter.empty()) {
        OpenMountedDriveInExplorer(client);
    }
    Log::Info("Android client connected: " + client.clientId + " (" + client.remoteIp + "), webDav=" +
              client.webDavHost + ":" + std::to_string(client.webDavPort) +
              ", mountReady=" + std::string(client.mountReady ? "true" : "false") +
              ", driveLetter=" + (client.driveLetter.empty() ? std::string("<none>") : client.driveLetter) +
              ", mountError=" + (client.mountError.empty() ? std::string("<none>") : client.mountError));

    std::ostringstream responseBody;
    responseBody << "{\"accepted\":true,\"clientId\":\"" << JsonEscape(client.clientId)
                 << "\",\"driveLetter\":\"" << JsonEscape(client.driveLetter) << "\",\"driveName\":\""
                 << JsonEscape(Utf::WideToUtf8(client.driveName))
                 << "\",\"mountReady\":" << (client.mountReady ? "true" : "false")
                 << ",\"mountError\":\"" << JsonEscape(client.mountError) << "\"}";
    return MakeJsonResponse(200, "OK", responseBody.str());
}

ControlRequestDisposition ControlServer::HandleSessionRequest(const HttpRequest &request, SOCKET clientSocket) {
    const std::string clientId = ExtractSessionClientId(request.path);
    if (clientId.empty()) {
        return ControlRequestDisposition::NotHandled;
    }

    if (!clientManager_.MarkSessionStarted(clientId)) {
        const std::string body = R"({"accepted":false,"error":"Unknown clientId"})";
        const std::string response =
            "HTTP/1.1 404 Not Found\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " +
            std::to_string(body.size()) + "\r\nConnection: close\r\n\r\n" + body;
        SendAll(clientSocket, response);
        closesocket(clientSocket);
        return ControlRequestDisposition::ResponseSentDirectly;
    }

    Log::Info("Android session started: " + clientId);

    const std::string headers =
        "HTTP/1.1 200 OK\r\n"
        "Content-Type: application/json; charset=utf-8\r\n"
        "Cache-Control: no-cache\r\n"
        "Connection: keep-alive\r\n\r\n";
    if (!SendAll(clientSocket, headers)) {
        if (const auto removedClient = clientManager_.RemoveClient(clientId); removedClient.has_value()) {
            WebDavDriveMapper::Unmount(*removedClient);
        }
        closesocket(clientSocket);
        return ControlRequestDisposition::ResponseSentDirectly;
    }

    const auto deadlineStep = Protocol::kControlHeartbeatInterval;
    auto lastSuccessfulSend = std::chrono::steady_clock::now();
    while (true) {
        const std::string heartbeat = "{\"heartbeat\":true,\"clientId\":\"" + JsonEscape(clientId) + "\"}\n";
        if (!SendAll(clientSocket, heartbeat)) {
            const auto now = std::chrono::steady_clock::now();
            if (now - lastSuccessfulSend >= Protocol::kControlDisconnectTimeout) {
                break;
            }
        } else {
            lastSuccessfulSend = std::chrono::steady_clock::now();
            clientManager_.TouchClient(clientId);
        }

        std::this_thread::sleep_for(deadlineStep);
        if (!clientManager_.Contains(clientId)) {
            break;
        }
    }

    if (const auto removedClient = clientManager_.RemoveClient(clientId); removedClient.has_value()) {
        WebDavDriveMapper::Unmount(*removedClient);
    }
    Log::Info("Android session closed: " + clientId);
    shutdown(clientSocket, SD_BOTH);
    closesocket(clientSocket);
    return ControlRequestDisposition::ResponseSentDirectly;
}
