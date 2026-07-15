#include "DiscoveryResponder.h"

#include "Protocol.h"
#include "utils/Log.h"
#include "utils/Utf.h"

#include <windows.h>
#include <ws2tcpip.h>

#include <array>
#include <string>

namespace {

constexpr char kPrimaryDiscoveryProbe[] = "DROPME_DISCOVER_V1";
constexpr char kLegacyDiscoveryProbe[] = "WIFIDROP_DISCOVER_V1";

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
            default:
                escaped.push_back(static_cast<char>(symbol));
                break;
        }
    }
    return escaped;
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

}  // namespace

void DiscoveryResponder::Start() {
    if (running_.exchange(true)) {
        return;
    }

    socket_ = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_ == INVALID_SOCKET) {
        running_ = false;
        Log::Warn("UDP discovery socket was not created");
        return;
    }

    BOOL reuseAddress = TRUE;
    setsockopt(socket_, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char *>(&reuseAddress), sizeof(reuseAddress));

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    address.sin_port = htons(Protocol::kDiscoveryPort);

    if (bind(socket_, reinterpret_cast<const sockaddr *>(&address), sizeof(address)) == SOCKET_ERROR) {
        Log::Warn("UDP discovery bind failed on port " + std::to_string(Protocol::kDiscoveryPort));
        closesocket(socket_);
        socket_ = INVALID_SOCKET;
        running_ = false;
        return;
    }

    thread_ = std::thread(&DiscoveryResponder::RunLoop, this);
    Log::Info("UDP discovery responder listening on port " + std::to_string(Protocol::kDiscoveryPort));
}

void DiscoveryResponder::Stop() {
    if (!running_.exchange(false)) {
        return;
    }

    if (socket_ != INVALID_SOCKET) {
        closesocket(socket_);
        socket_ = INVALID_SOCKET;
    }

    if (thread_.joinable()) {
        thread_.join();
    }
}

void DiscoveryResponder::RunLoop() {
    std::array<char, 1024> buffer{};
    while (running_) {
        sockaddr_in remoteAddress{};
        int remoteAddressSize = sizeof(remoteAddress);
        const int received = recvfrom(socket_,
                                      buffer.data(),
                                      static_cast<int>(buffer.size()),
                                      0,
                                      reinterpret_cast<sockaddr *>(&remoteAddress),
                                      &remoteAddressSize);
        if (received <= 0) {
            continue;
        }

        const std::string payload(buffer.data(), received);
        if (payload != kPrimaryDiscoveryProbe && payload != kLegacyDiscoveryProbe) {
            continue;
        }

        char remoteIp[INET_ADDRSTRLEN]{};
        InetNtopA(AF_INET, &remoteAddress.sin_addr, remoteIp, INET_ADDRSTRLEN);
        Log::Info(std::string("UDP discovery request received from ") + remoteIp);

        const std::string response =
            "{\"app\":\"DROPME\",\"role\":\"windows-server\",\"protocolVersion\":" +
            std::to_string(Protocol::kProtocolVersion) +
            ",\"deviceName\":\"" + JsonEscape(GetComputerNameUtf8()) +
            "\",\"tcpPort\":" + std::to_string(Protocol::kTcpPort) + "}";

        sendto(socket_,
               response.data(),
               static_cast<int>(response.size()),
               0,
               reinterpret_cast<const sockaddr *>(&remoteAddress),
               remoteAddressSize);
    }
}
