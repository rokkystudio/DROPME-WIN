#pragma once

#include "HttpTypes.h"

/// Обрабатывает HTTP upload endpoint и сохраняет файлы в папку рабочего стола.
class UploadController {
public:
    /// Пытается обработать PUT /dropme/upload и формирует HTTP-ответ.
    bool HandleRequest(const HttpRequest &request, HttpResponse &response) const;
};
