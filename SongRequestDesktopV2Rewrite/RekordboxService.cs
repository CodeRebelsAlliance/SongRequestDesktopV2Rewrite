using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace SongRequestDesktopV2Rewrite
{
    class RekordboxService
    {
        public static void AddTrackToRekordbox(string trackPath, string creator)
        {
            string trackName = Path.GetFileNameWithoutExtension(trackPath);
            string trackLocation = "file://localhost/" + trackPath.Replace("\\", "/");

            // Create the Rekordbox XML structure
            XDocument xml = new XDocument(
                new XElement("rekordbox",
                    new XElement("COLLECTION",
                        new XElement("TRACK",
                            new XAttribute("TrackID", "1"),
                            new XAttribute("Name", trackName),
                            new XAttribute("Artist", creator),
                            new XAttribute("Album", "Unknown Album"),
                            new XAttribute("Genre", "Unknown Genre"),
                            new XAttribute("Kind", "MP3 File"),
                            new XAttribute("Size", new FileInfo(trackPath).Length),
                            new XAttribute("TotalTime", "0"), // You may need to calculate the track length
                            new XAttribute("Location", trackLocation),
                            new XAttribute("CuePoint", "0"),
                            new XAttribute("BitRate", "320"), // Assuming 320 kbps
                            new XAttribute("SampleRate", "44100") // Assuming 44100 Hz
                        )
                    )
                )
            );

            // Save the XML to a file
            string xmlPath = Path.Combine(Path.GetTempPath(), "rekordbox_import.xml");
            xml.Save(xmlPath);

            // Import the XML file into Rekordbox
            ImportXMLToRekordbox(xmlPath);
        }

        public static void CopyFile(string sourceFilePath, string destinationDirectory, string newFileName)
        {
            try
            {
                // Check if the source file exists
                if (!File.Exists(sourceFilePath))
                {
                    throw new FileNotFoundException("Source file not found.", sourceFilePath);
                }

                // Ensure the destination directory exists
                if (!Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                // Combine the destination directory with the new file name to get the full destination path
                string destinationFilePath = Path.Combine(destinationDirectory, newFileName);

                // Copy the file to the new location
                File.Copy(sourceFilePath, destinationFilePath, overwrite: true);

                Console.WriteLine($"File copied successfully from {sourceFilePath} to {destinationFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }

        private static void ImportXMLToRekordbox(string xmlPath)
        {
            // Assuming Rekordbox has a command line interface for importing XML
            try
            {
                string rekordboxPath = @"C:\Program Files\rekordbox\rekordbox.exe"; // Update with the correct path

                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = rekordboxPath,
                    Arguments = $"-import {xmlPath}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
                {
                    using (StreamReader reader = process.StandardOutput)
                    {
                        string result = reader.ReadToEnd();
                        Console.WriteLine(result);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
    }
}
