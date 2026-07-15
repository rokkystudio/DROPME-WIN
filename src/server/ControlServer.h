#pragma once

#include "clients/ClientManager.h"
#include "HttpTypes.h"

#include <winsock2.h>

/// Описывает результат маршрутизации control-запроса.
enum class ControlRequestDisposition {
    NotHandled,
    ResponseReady,
    ResponseSentDirectly,
};

/// Обрабатывает control endpoints DROPME и управляет жизненным циклом Android-сессий.
class ControlServer {
public:
    /// Создаёт обработчик control-запросов с доступом к менеджеру Android-клиентов.
    explicit ControlServer(ClientManager &clientManager);

    /// Маршрутизирует control-запрос и либо формирует обычный ответ, либо удерживает session-соединение.
    ControlRequestDisposition HandleRequest(const HttpRequest &request, SOCKET clientSocket, HttpResponse &response);

private:
    /// Формирует ответ для GET /dropme/info.
    HttpResponse HandleInfoRequest() const;

    /// Формирует ответ для POST /dropme/client/connect.
    HttpResponse HandleConnectRequest(const HttpRequest &request);

    /// Удерживает persistent session, пока Android не отключится.
    ControlRequestDisposition HandleSessionRequest(const HttpRequest &request, SOCKET clientSocket);

    ClientManager &clientManager_;
};
