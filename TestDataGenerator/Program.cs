using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TestDataGenerator
{
    class Program
    {
        static void Main(string[] args)
        {
            const string dir = "data";

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            else
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    File.Delete(file);
                }
            }

            GenerateDb2(dir);
        }

        private static void GenerateDb2(string dir)
        {
            // Generate a db with 50 tables.
            using (FileStream fsCreate = File.Open(Path.Combine(dir, "Db2_Create.sql"), FileMode.Create, FileAccess.ReadWrite))
            using (StreamWriter swCreate = new StreamWriter(fsCreate))
            {
                CultureInfo enUs = new CultureInfo("en-US");

                Random rnd = new Random();
                List<string> data = new List<string>
                {
                    "World", "Rocks", "My", "Banana", "Apple", "Monkey", "Elephant", "Thousand", "Skies", "Republic", "Ramadan"
                };

                byte[] binaryData = new byte[32];

                for (int tbl = 0; tbl < 10; tbl++)
                {
                    swCreate.WriteLine($"create table Table{tbl:00} (MyInt INTEGER, MyReal REAL, MyText TEXT, MyBlob BLOB);");
                    
                    using (FileStream fsData = File.Open(Path.Combine(dir, $"Data-{tbl:00}.csv"), FileMode.Create))
                    using (StreamWriter swData = new StreamWriter(fsData))
                    {
                        for (int i = 0; i < 500; i++)
                        {
                            int a = rnd.Next(int.MinValue, int.MaxValue);
                            double b = rnd.NextDouble();

                            string c = "";
                            for (int k = 0; k < 5; k++)
                            {
                                if (k > 0)
                                    c += " ";
                                c += data[rnd.Next(0, data.Count)];
                            }

                            rnd.NextBytes(binaryData);
                            string d = BitConverter.ToString(binaryData).Replace("-", "");

                            swData.Write(a);
                            swData.Write(";");
                            swData.Write(b.ToString(enUs));
                            swData.Write(";");
                            swData.Write(c);
                            swData.Write(";");
                            swData.Write(d);
                            swData.WriteLine();
                        }
                    }
                }

                for (int tbl = 10; tbl < 20; tbl++)
                {
                    swCreate.WriteLine($"create table Table{tbl:00} (MyInt1 INTEGER, MyInt2 INTEGER, MyInt3 INTEGER, MyInt4 INTEGER);");

                    using (FileStream fsData = File.Open(Path.Combine(dir, $"Data-{tbl:00}.csv"), FileMode.Create))
                    using (StreamWriter swData = new StreamWriter(fsData))
                    {
                        for (int i = 0; i < 500; i++)
                        {
                            swData.Write(rnd.Next());
                            swData.Write(";");
                            swData.Write(rnd.Next());
                            swData.Write(";");
                            swData.Write(rnd.Next());
                            swData.Write(";");
                            swData.Write(rnd.Next());
                            swData.WriteLine();
                        }
                    }
                }

                for (int tbl = 20; tbl < 30; tbl++)
                {
                    swCreate.WriteLine($"create table Table{tbl:00} (MyReal1 REAL, MyReal2 REAL, MyReal3 REAL, MyReal4 REAL);");

                    using (FileStream fsData = File.Open(Path.Combine(dir, $"Data-{tbl:00}.csv"), FileMode.Create))
                    using (StreamWriter swData = new StreamWriter(fsData))
                    {
                        for (int i = 0; i < 500; i++)
                        {
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.Write(";");
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.Write(";");
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.Write(";");
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.WriteLine();
                        }
                    }
                }

                for (int tbl = 30; tbl < 40; tbl++)
                {
                    swCreate.WriteLine($"create table Table{tbl:00} (MyText1 TEXT, MyText2 TEXT, MyText3 TEXT, MyText4 TEXT);");

                    using (FileStream fsData = File.Open(Path.Combine(dir, $"Data-{tbl:00}.csv"), FileMode.Create))
                    using (StreamWriter swData = new StreamWriter(fsData))
                    {
                        for (int i = 0; i < 500; i++)
                        {
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.Write(";");
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.Write(";");
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.Write(";");
                            swData.Write(rnd.NextDouble().ToString(enUs));
                            swData.WriteLine();

                            for (int u = 0; u < 4; u++)
                            {
                                if (u > 0)
                                    swData.Write(";");

                                swData.Write("\"");
                                for (int k = 0; k < 5; k++)
                                {
                                    if (k > 0)
                                        swData.Write(" ");
                                    swData.Write(data[rnd.Next(0, data.Count)]);
                                }
                                swData.Write("\"");
                            }
                            swData.WriteLine();
                        }
                    }
                }

                for (int tbl = 40; tbl < 50; tbl++)
                {
                    swCreate.WriteLine($"create table Table{tbl:00} (MyBlob1 BLOB, MyBlob2 BLOB, MyBlob3 BLOB, MyBlob4 BLOB);");

                    using (FileStream fsData = File.Open(Path.Combine(dir, $"Data-{tbl:00}.csv"), FileMode.Create))
                    using (StreamWriter swData = new StreamWriter(fsData))
                    {
                        for (int j = 0; j < 500; j++)
                        {
                            for (int k = 0; k < 4; k++)
                            {
                                if (k > 0)
                                    swData.Write(";");

                                //swData.Write("X'");
                                rnd.NextBytes(binaryData);
                                swData.Write(BitConverter.ToString(binaryData).Replace("-", ""));
                                //swData.Write("'");
                            }

                            swData.WriteLine();
                        }
                    }
                }
                
                swCreate.WriteLine();
                swCreate.WriteLine(".separator ';'");

                for (int tbl = 0; tbl < 50; tbl++)
                {
                    swCreate.WriteLine($".print Table{tbl:00}");
                    swCreate.WriteLine($".import Data-{tbl:00}.csv Table{tbl:00}");
                }
            }
        }
    }
}