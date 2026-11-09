using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using StackExchange.Redis;

namespace HospitalNoSqlLab2
{
    // --- Моделі MongoDB ---
    public class MongoAppointment
    {
        [BsonId] public ObjectId Id { get; set; }
        public int PatientId { get; set; }
        public int DoctorId { get; set; }
        public DateTime AppointmentDate { get; set; }
        public string Status { get; set; }
        public decimal Price { get; set; }
        public MongoClinicalRecord ClinicalRecord { get; set; }
    }

    public class MongoClinicalRecord
    {
        public string Symptoms { get; set; }
        public string DiagnosisCode { get; set; }
        public string DiagnosisName { get; set; }
        public List<MongoPrescriptionItem> Prescriptions { get; set; } = new();
        public List<MongoLabOrder> LabOrders { get; set; } = new();
    }

    public class MongoPrescriptionItem
    {
        public string MedicationName { get; set; }
        public string Dosage { get; set; }
        public string Frequency { get; set; }
        public int DurationDays { get; set; }
    }

    public class MongoLabOrder
    {
        public string TestCode { get; set; }
        public string TestName { get; set; }
        public string Result { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("Оберіть дію:");
            Console.WriteLine("1 - Стрес-тест швидкодії (MongoDB vs MS SQL Server)");
            Console.WriteLine("2 - Демонстрація операцій Key-Value (Redis)");
            Console.Write("\nВаш вибір (1 або 2): ");

            string choice = Console.ReadLine();

            if (choice == "2")
            {
                RunRedisDemo();
            }
            else
            {
                RunMongoBenchmark();
            }

            Console.WriteLine("\nНатисніть будь-яку клавішу для завершення...");
            Console.ReadKey();
        }

        // === ТЕСТ REDIS ===
        static void RunRedisDemo()
        {
            Console.WriteLine("\n=== ДЕМОНСТРАЦІЯ ОПЕРАЦІЙ REDIS ДЛЯ ЛІКАРНІ ===\n");
            try
            {
                ConnectionMultiplexer redis = ConnectionMultiplexer.Connect("localhost:6379");
                IDatabase db = redis.GetDatabase();

                // 1. Сесія лікаря (Key-Value з TTL)
                string sessionKey = "session:doctor:104";
                db.StringSet(sessionKey, "Коваль Тарас Іванович | Спеціалізація: Терапевт", TimeSpan.FromMinutes(15));
                Console.WriteLine($"[1] Створено токен сесії лікаря (TTL 15 хв): \n    {db.StringGet(sessionKey)}\n");

                // 2. Електронна черга до лікаря (Структура LIST / FIFO)
                string queueKey = "queue:doctor:104";
                db.KeyDelete(queueKey);

                db.ListRightPush(queueKey, "Пацієнт #1: Шевченко Б. В.");
                db.ListRightPush(queueKey, "Пацієнт #2: Бондаренко О. П.");
                db.ListRightPush(queueKey, "Пацієнт #3: Ткаченко М. С.");

                Console.WriteLine($"[2] Додано 3 пацієнтів у чергу. Загальна довжина черги: {db.ListLength(queueKey)}");

                string nextPatient = db.ListLeftPop(queueKey);
                Console.WriteLine($" -> Лікар натиснув 'Викликати наступного': Заходить [{nextPatient}]");
                Console.WriteLine($" -> Залишилось у черзі: {db.ListLength(queueKey)} люд.\n");

                // 3. Лічильник зайнятих ліжок (Структура HASH)
                string wardKey = "ward:occupancy:therapy";
                db.HashSet(wardKey, new HashEntry[] {
                    new HashEntry("TotalBeds", 20),
                    new HashEntry("OccupiedBeds", 17)
                });

                db.HashDecrement(wardKey, "OccupiedBeds", 1);
                var occupied = db.HashGet(wardKey, "OccupiedBeds");
                Console.WriteLine($"[3] Моніторинг палат у реальному часі (Відділення терапії):");
                Console.WriteLine($" -> Зайнято ліжок: {occupied} з 20");

                Console.WriteLine("\n>>> Усі операції в Redis успішно відпрацювали! <<<");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Помилка підключення до Redis]: {ex.Message}");
                Console.ResetColor();
            }
        }

        // === ТЕСТ MONGODB vs SQL ===
        static void RunMongoBenchmark()
        {
            const int RecordsCount = 10000;
            const string SqlConnectionString = "Server=.\\SQLEXPRESS;Database=HospitalDb;Integrated Security=True;TrustServerCertificate=True;";
            const string MongoConnectionString = "mongodb://localhost:27017";

            Console.WriteLine($"\n=== СТРЕС-ТЕСТ ШВИДКОДІЇ: MS SQL vs MONGODB ({RecordsCount} записів) ===\n");

            try
            {
                // 1. Отримуємо реально існуючі ID пацієнтів, лікарів та діагнозу із MS SQL
                var patientIds = new List<int>();
                var doctorIds = new List<int>();
                int validDiagnosisId = 1;

                using (var sqlConn = new SqlConnection(SqlConnectionString))
                {
                    sqlConn.Open();

                    using (var cmd = new SqlCommand("SELECT Id FROM Patients WHERE IsDeleted = 0", sqlConn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) patientIds.Add(r.GetInt32(0));
                    }

                    using (var cmd = new SqlCommand("SELECT Id FROM Doctors WHERE IsDeleted = 0", sqlConn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) doctorIds.Add(r.GetInt32(0));
                    }

                    // Перевіряємо та гарантуємо наявність діагнозу
                    using (var cmd = new SqlCommand("SELECT TOP 1 Id FROM Diagnoses", sqlConn))
                    {
                        var obj = cmd.ExecuteScalar();
                        if (obj != null)
                        {
                            validDiagnosisId = Convert.ToInt32(obj);
                        }
                        else
                        {
                            using var insCmd = new SqlCommand(
                                "INSERT INTO Diagnoses (Code, Name) OUTPUT INSERTED.Id VALUES ('I10', N'Есенціальна гіпертензія');", sqlConn);
                            validDiagnosisId = (int)insCmd.ExecuteScalar();
                        }
                    }
                }

                if (patientIds.Count == 0 || doctorIds.Count == 0)
                {
                    Console.WriteLine("[Увага]: У базі SQL немає пацієнтів або лікарів. Запустіть спочатку скрипти з Лаб 1.");
                    return;
                }

                // 2. Ініціалізація MongoDB
                var mongoClient = new MongoClient(MongoConnectionString);
                var mongoDb = mongoClient.GetDatabase("HospitalNoSqlDb");
                var mongoCollection = mongoDb.GetCollection<MongoAppointment>("ClinicalEncounters");
                mongoDb.DropCollection("ClinicalEncounters");

                mongoCollection.Indexes.CreateOne(new CreateIndexModel<MongoAppointment>(
                    Builders<MongoAppointment>.IndexKeys.Ascending(x => x.PatientId)));

                Console.WriteLine("[Підготовка] Генерація тестових карток...");
                var mongoData = new List<MongoAppointment>(RecordsCount);
                for (int i = 0; i < RecordsCount; i++)
                {
                    mongoData.Add(new MongoAppointment
                    {
                        PatientId = patientIds[i % patientIds.Count],
                        DoctorId = doctorIds[i % doctorIds.Count],
                        AppointmentDate = DateTime.UtcNow.AddMinutes(-i),
                        Status = "Completed",
                        Price = 500.00m,
                        ClinicalRecord = new MongoClinicalRecord
                        {
                            Symptoms = "Скарги на підвищений тиск",
                            DiagnosisCode = "I10",
                            DiagnosisName = "Есенціальна гіпертензія",
                            Prescriptions = new List<MongoPrescriptionItem>
                            {
                                new() { MedicationName = "Амлодипін", Dosage = "10 мг", Frequency = "1 р/д", DurationDays = 30 }
                            },
                            LabOrders = new List<MongoLabOrder>
                            {
                                new() { TestCode = "BL-01", TestName = "Загальний аналіз крові", Result = "Норма" }
                            }
                        }
                    });
                }

                // 3. Запис у MongoDB
                var sw = Stopwatch.StartNew();
                mongoCollection.InsertMany(mongoData);
                sw.Stop();
                long mongoInsertTime = sw.ElapsedMilliseconds;
                Console.WriteLine($"\n[MongoDB] Запис {RecordsCount} документів: {mongoInsertTime} мс");

                // 4. Запис у MS SQL (з гарантованим validDiagnosisId)
                sw.Restart();
                using (var sqlConn = new SqlConnection(SqlConnectionString))
                {
                    sqlConn.Open();
                    using var trans = sqlConn.BeginTransaction();
                    for (int i = 0; i < RecordsCount; i++)
                    {
                        int pId = patientIds[i % patientIds.Count];
                        int dId = doctorIds[i % doctorIds.Count];

                        using var cmdApp = new SqlCommand(
                            "INSERT INTO Appointments (PatientId, DoctorId, AppointmentDate, Price, Status) " +
                            "OUTPUT INSERTED.Id VALUES (@pId, @dId, @date, @price, 'Completed');", sqlConn, trans);
                        cmdApp.Parameters.AddWithValue("@pId", pId);
                        cmdApp.Parameters.AddWithValue("@dId", dId);
                        cmdApp.Parameters.AddWithValue("@date", DateTime.UtcNow);
                        cmdApp.Parameters.AddWithValue("@price", 500.00m);
                        int appId = (int)cmdApp.ExecuteScalar();

                        using var cmdMed = new SqlCommand(
                            "INSERT INTO MedicalRecords (AppointmentId, DiagnosisId, Symptoms) " +
                            "VALUES (@appId, @diagId, 'Скарги на тиск');", sqlConn, trans);
                        cmdMed.Parameters.AddWithValue("@appId", appId);
                        cmdMed.Parameters.AddWithValue("@diagId", validDiagnosisId);
                        cmdMed.ExecuteNonQuery();
                    }
                    trans.Commit();
                }
                sw.Stop();
                long sqlInsertTime = sw.ElapsedMilliseconds;
                Console.WriteLine($"[MS SQL]  Запис {RecordsCount} записів (2 таблиці): {sqlInsertTime} мс");

                // 5. Читання з MongoDB
                sw.Restart();
                for (int i = 0; i < 1000; i++)
                {
                    int targetPatientId = patientIds[i % patientIds.Count];
                    var doc = mongoCollection.Find(x => x.PatientId == targetPatientId).FirstOrDefault();
                }
                sw.Stop();
                long mongoReadTime = sw.ElapsedMilliseconds;
                Console.WriteLine($"\n[MongoDB] 1000 пошуків документа: {mongoReadTime} мс");

                // 6. Читання з MS SQL
                sw.Restart();
                using (var sqlConn = new SqlConnection(SqlConnectionString))
                {
                    sqlConn.Open();
                    for (int i = 0; i < 1000; i++)
                    {
                        int targetPatientId = patientIds[i % patientIds.Count];
                        using var cmd = new SqlCommand(@"
                            SELECT TOP 1 a.Id, a.AppointmentDate, mr.Symptoms, d.Name AS Diagnosis
                            FROM Appointments a
                            INNER JOIN MedicalRecords mr ON a.Id = mr.AppointmentId
                            INNER JOIN Diagnoses d ON mr.DiagnosisId = d.Id
                            WHERE a.PatientId = @targetId", sqlConn);
                        cmd.Parameters.AddWithValue("@targetId", targetPatientId);
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read()) { }
                    }
                }
                sw.Stop();
                long sqlReadTime = sw.ElapsedMilliseconds;
                Console.WriteLine($"[MS SQL]  1000 пошуків через JOIN: {sqlReadTime} мс");

                Console.WriteLine("\n=== ПІДСУМОК ===");

                // 1. Аналіз запису
                if (mongoInsertTime < sqlInsertTime)
                {
                    double times = Math.Round((double)sqlInsertTime / mongoInsertTime, 1);
                    Console.WriteLine($"Запис:   MongoDB швидша у ~{times} рази");
                }
                else
                {
                    double times = Math.Round((double)mongoInsertTime / sqlInsertTime, 1);
                    Console.WriteLine($"Запис:   MS SQL швидший у ~{times} рази");
                }

                // 2. Аналіз читання
                if (mongoReadTime < sqlReadTime)
                {
                    double times = Math.Round((double)sqlReadTime / mongoReadTime, 1);
                    Console.WriteLine($"Читання: MongoDB швидша у ~{times} рази");
                }
                else
                {
                    double times = Math.Round((double)mongoReadTime / sqlReadTime, 1);
                    Console.WriteLine($"Читання: MS SQL швидший у ~{times} рази");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Помилка]: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}