using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DROPME.Clients
{
    /// <summary>
    /// Управляет подключенными Android-устройствами и жизненным циклом их control session.
    /// Для timeout используется монотонный Stopwatch, соответствующий std::chrono::steady_clock исходного приложения.
    /// </summary>
    internal sealed class ClientManager
    {
        private readonly object mutex_ = new object();
        private readonly Dictionary<string, AndroidClient> clients_ = new Dictionary<string, AndroidClient>(StringComparer.Ordinal);

        /// <summary>
        /// Добавляет клиента или заменяет существующую запись с тем же clientId.
        /// </summary>
        public void AddClient(AndroidClient client)
        {
            lock (mutex_)
            {
                clients_[client.ClientId] = client.Clone();
            }
        }

        /// <summary>
        /// Помечает начало persistent session для клиента и обновляет монотонный timestamp активности.
        /// </summary>
        public bool MarkSessionStarted(string clientId)
        {
            lock (mutex_)
            {
                if (!clients_.TryGetValue(clientId, out AndroidClient? client))
                {
                    return false;
                }

                client.SessionState = AndroidSessionState.Active;
                client.LastActivityTimestamp = Stopwatch.GetTimestamp();
                return true;
            }
        }

        /// <summary>
        /// Обновляет монотонный timestamp активности клиента.
        /// </summary>
        public bool TouchClient(string clientId)
        {
            lock (mutex_)
            {
                if (!clients_.TryGetValue(clientId, out AndroidClient? client))
                {
                    return false;
                }

                client.LastActivityTimestamp = Stopwatch.GetTimestamp();
                return true;
            }
        }

        /// <summary>
        /// Возвращает признак наличия клиента в менеджере.
        /// </summary>
        public bool Contains(string clientId)
        {
            lock (mutex_)
            {
                return clients_.ContainsKey(clientId);
            }
        }

        /// <summary>
        /// Удаляет клиента по идентификатору и возвращает его последнее состояние.
        /// </summary>
        public AndroidClient? RemoveClient(string clientId)
        {
            lock (mutex_)
            {
                if (!clients_.TryGetValue(clientId, out AndroidClient? client))
                {
                    return null;
                }

                clients_.Remove(clientId);
                return client.Clone();
            }
        }

        /// <summary>
        /// Возвращает независимые копии всех активных клиентских записей.
        /// </summary>
        public List<AndroidClient> ListClients()
        {
            lock (mutex_)
            {
                List<AndroidClient> result = new List<AndroidClient>(clients_.Count);
                foreach (AndroidClient client in clients_.Values)
                {
                    result.Add(client.Clone());
                }
                return result;
            }
        }

        /// <summary>
        /// Удаляет клиентов, у которых истёк timeout монотонной активности.
        /// </summary>
        public List<AndroidClient> RemoveInactive(TimeSpan timeout)
        {
            long now = Stopwatch.GetTimestamp();
            long timeoutTicks = (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            List<AndroidClient> removed = new List<AndroidClient>();

            lock (mutex_)
            {
                List<string> ids = new List<string>();
                foreach (KeyValuePair<string, AndroidClient> pair in clients_)
                {
                    if (now - pair.Value.LastActivityTimestamp >= timeoutTicks)
                    {
                        ids.Add(pair.Key);
                        removed.Add(pair.Value.Clone());
                    }
                }

                foreach (string id in ids)
                {
                    clients_.Remove(id);
                }
            }

            return removed;
        }

        /// <summary>
        /// Очищает все клиентские записи и возвращает их последние состояния.
        /// </summary>
        public List<AndroidClient> Clear()
        {
            lock (mutex_)
            {
                List<AndroidClient> removed = new List<AndroidClient>(clients_.Count);
                foreach (AndroidClient client in clients_.Values)
                {
                    removed.Add(client.Clone());
                }
                clients_.Clear();
                return removed;
            }
        }
    }
}
