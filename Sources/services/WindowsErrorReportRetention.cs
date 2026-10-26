using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DROPME.Services
{
    /// <summary>
    /// Ограничивает число Windows Error Reporting crash reports для приложения и срок их хранения.
    /// </summary>
    internal static class WindowsErrorReportRetention
    {
        private const int RetentionDays = 7;

        /// <summary>
        /// Удаляет crash reports Windows Error Reporting старше семи дней и сохраняет не более одного самого нового отчёта.
        /// Проверяются очереди ReportQueue и ReportArchive в общем каталоге ProgramData.
        /// Ошибки доступа к системным каталогам не прерывают запуск приложения.
        /// </summary>
        /// <param name="executableName">Имя исполняемого файла вместе с расширением.</param>
        public static void Trim(string executableName)
        {
            var reports = FindReports(executableName);
            var cutoffUtc = DateTime.UtcNow.AddDays(-RetentionDays);

            foreach (var report in reports.Where(report => report.LastWriteTimeUtc < cutoffUtc))
            {
                Delete(report);
            }

            foreach (var report in reports
                .Where(report => report.Exists && report.LastWriteTimeUtc >= cutoffUtc)
                .OrderByDescending(report => report.LastWriteTimeUtc)
                .Skip(1))
            {
                Delete(report);
            }
        }

        /// <summary>
        /// Возвращает доступные каталоги crash reports Windows Error Reporting для указанного приложения.
        /// </summary>
        private static List<DirectoryInfo> FindReports(string executableName)
        {
            var reports = new List<DirectoryInfo>();
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var werRoot = Path.Combine(programData, "Microsoft", "Windows", "WER");
            var pattern = "AppCrash_" + executableName + "_*";

            foreach (var reportRootName in new[] { "ReportQueue", "ReportArchive" })
            {
                var reportRoot = Path.Combine(werRoot, reportRootName);
                if (!Directory.Exists(reportRoot))
                {
                    continue;
                }

                try
                {
                    reports.AddRange(new DirectoryInfo(reportRoot).EnumerateDirectories(pattern, SearchOption.TopDirectoryOnly));
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }

            return reports;
        }

        /// <summary>
        /// Удаляет каталог crash report, если Windows Error Reporting не удерживает его открытым и текущему пользователю разрешено удаление.
        /// </summary>
        private static void Delete(DirectoryInfo report)
        {
            try
            {
                if (report.Exists)
                {
                    report.Delete(true);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}
