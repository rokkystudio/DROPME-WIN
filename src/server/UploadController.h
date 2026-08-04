#pragma once

#include "HttpTypes.h"

#include <chrono>
#include <filesystem>
#include <mutex>
#include <string>
#include <unordered_map>

/// Обрабатывает HTTP upload endpoint и сохраняет файлы в папку рабочего стола.
class UploadController {
public:
    /// Пытается обработать PUT /dropme/upload и формирует HTTP-ответ.
    bool HandleRequest(const HttpRequest &request, HttpResponse &response) const;

private:
    struct UploadBatchState {
        std::filesystem::path folder;
        std::chrono::steady_clock::time_point lastActivity;
    };

    std::filesystem::path ResolveIncomingFolder(const HttpRequest &request) const;

    mutable std::mutex uploadBatchesMutex_;
    mutable std::unordered_map<std::string, UploadBatchState> uploadBatches_;
};
