﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Passbook.Generator.Fields;
using Passbook.Generator.Exceptions;
using System.Security.Cryptography.Pkcs;

namespace Passbook.Generator
{
    public class PassGenerator
    {
        private byte[] passFile = null;
        private byte[] signatureFile = null;
        private byte[] manifestFile = null;
        private byte[] pkPassFile = null;

        private const string APPLE_CERTIFICATE_THUMBPRINT = "‎0950b6cd3d2f37ea246a1aaa20dfaadbd6fe1f75";

        public byte[] Generate(PassGeneratorRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request", "You must pass an instance of PassGeneratorRequest");

            if (request.IsValid)
            {
                CreatePackage(request);
                ZipPackage(request);

                return pkPassFile;
            }
            else
                throw new Exception("PassGeneratorRequest is not valid");
        }

        private void ZipPackage(PassGeneratorRequest request)
        {
            using (MemoryStream zipToOpen = new MemoryStream())
            {
                using (ZipArchive archive = new ZipArchive(zipToOpen, ZipArchiveMode.Update, true))
                {
					ZipArchiveEntry imageEntry = null;

					foreach (KeyValuePair<PassbookImage, byte[]> image in request.Images)
					{	
						imageEntry = archive.CreateEntry(image.Key.ToFilename());

						using (BinaryWriter writer = new BinaryWriter(imageEntry.Open()))
						{
							writer.Write(image.Value);
							writer.Flush();
						}
					}

                    ZipArchiveEntry PassJSONEntry = archive.CreateEntry(@"pass.json");
                    using (BinaryWriter writer = new BinaryWriter(PassJSONEntry.Open()))
                    {
                        writer.Write(passFile);
                        writer.Flush();
                    }

                    ZipArchiveEntry ManifestJSONEntry = archive.CreateEntry(@"manifest.json");
                    using (BinaryWriter writer = new BinaryWriter(ManifestJSONEntry.Open()))
                    {
                        writer.Write(manifestFile);
                        writer.Flush();
                    }

                    ZipArchiveEntry SignatureEntry = archive.CreateEntry(@"signature");
                    using (BinaryWriter writer = new BinaryWriter(SignatureEntry.Open()))
                    {
                        writer.Write(signatureFile);
                        writer.Flush();
                    }
                }

                pkPassFile = zipToOpen.ToArray();
                zipToOpen.Flush();
            }
        }

        private void CreatePackage(PassGeneratorRequest request)
        {
            CreatePassFile(request);
            GenerateManifestFile(request);
        }

        private void CreatePassFile(PassGeneratorRequest request)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (StreamWriter sr = new StreamWriter(ms))
                {
                    using (JsonWriter writer = new JsonTextWriter(sr))
                    {
                        Trace.TraceInformation("Writing JSON...");
                        request.Write(writer);
                    }

                    passFile = ms.ToArray();
                }
            }
        }

        private void GenerateManifestFile(PassGeneratorRequest request)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (StreamWriter sw = new StreamWriter(ms))
                {
                    using (JsonWriter jsonWriter = new JsonTextWriter(sw))
                    {
                        jsonWriter.Formatting = Formatting.Indented;
                        jsonWriter.WriteStartObject();

                        string hash = null;

						foreach (KeyValuePair<PassbookImage, byte[]> image in request.Images)
						{
							hash = GetHashForBytes(image.Value);
							jsonWriter.WritePropertyName(image.Key.ToFilename());
							jsonWriter.WriteValue(hash.ToLower());
						}

                        hash = GetHashForBytes(passFile);
                        jsonWriter.WritePropertyName(@"pass.json");
                        jsonWriter.WriteValue(hash.ToLower());
                    }

                    manifestFile = ms.ToArray();
                }

				SignManifestFile(request);
            }
        }

		private void SignManifestFile(PassGeneratorRequest request)
		{
			Trace.TraceInformation("Signing the manifest file...");

			X509Certificate2 card = GetCertificate(request);

			if (card == null)
				throw new FileNotFoundException("Certificate could not be found. Please ensure the thumbprint and cert location values are correct.");

			X509Certificate2 appleCA = GetAppleCertificate(request);

			if (appleCA == null)
				throw new FileNotFoundException("Apple Certificate could not be found. Please download it from http://www.apple.com/certificateauthority/ and install it into your LOCAL MACHINE certificate store.");

			try
			{
				ContentInfo contentInfo = new ContentInfo(manifestFile);

				SignedCms signing = new SignedCms(contentInfo, true);

				CmsSigner signer = new CmsSigner(SubjectIdentifierType.SubjectKeyIdentifier, card)
				{
					IncludeOption = X509IncludeOption.None
				};

				Trace.TraceInformation("Fetching Apple Certificate for signing..");
				Trace.TraceInformation("Constructing the certificate chain..");
				signer.Certificates.Add(appleCA);
				signer.Certificates.Add(card);

				signer.SignedAttributes.Add(new Pkcs9SigningTime());

				Trace.TraceInformation("Processing the signature..");
				signing.ComputeSignature(signer);

				signatureFile = signing.Encode();

				Trace.TraceInformation("The file has been successfully signed!");
			}
			catch (Exception exp)
			{
				Trace.TraceError("Failed to sign the manifest file: [{0}]", exp.Message);
				throw new ManifestSigningException("Failed to sign manifest", exp);
			}
		}

        private X509Certificate2 GetAppleCertificate(PassGeneratorRequest request)
        {
            Trace.TraceInformation("Fetching Apple Certificate...");

            try
            {
                if (request.AppleWWDRCACertificate == null)
                    return GetSpecifiedCertificateFromCertStore(APPLE_CERTIFICATE_THUMBPRINT, StoreName.CertificateAuthority, StoreLocation.LocalMachine);
                else
                    return GetCertificateFromBytes(request.AppleWWDRCACertificate, null);
            }
            catch (Exception exp)
            {
                Trace.TraceError("Failed to fetch Apple Certificate: [{0}]", exp.Message);
                throw;
            }
        }

        public static X509Certificate2 GetCertificate(PassGeneratorRequest request)
        {
            Trace.TraceInformation("Fetching Pass Certificate...");

            try
            {
                if (request.Certificate == null)
                    return GetSpecifiedCertificateFromCertStore(request.CertThumbprint, StoreName.My, request.CertLocation);
                else
                    return GetCertificateFromBytes(request.Certificate, request.CertificatePassword);
            }
            catch (Exception exp)
            {
                Trace.TraceError("Failed to fetch Pass Certificate: [{0}]", exp.Message);
                throw;
            }
        }

        private static X509Certificate2 GetSpecifiedCertificateFromCertStore(string thumbPrint, StoreName storeName, StoreLocation storeLocation)
        {
            X509Store store = new X509Store(storeName, storeLocation);
            store.Open(OpenFlags.ReadOnly);

			X509Certificate2Collection certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbPrint, true);

            if (certs.Count > 0)
            {
				Debug.WriteLine(certs[0].Thumbprint);
				return certs[0];
            }

            return null;
        }

        private static X509Certificate2 GetCertificateFromBytes(byte[] bytes, string password)
        {
            Trace.TraceInformation("Opening Certificate: [{0}] bytes with password [{1}]", bytes.Length, password);

            X509Certificate2 certificate = null;

            if (password == null)
                certificate = new X509Certificate2(bytes);
            else
            {
                X509KeyStorageFlags flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable;
                certificate = new X509Certificate2(bytes, password, flags);
            }

            return certificate;
        }

		private string GetHashForBytes(byte[] bytes)
		{
			using (SHA1CryptoServiceProvider oSHA1Hasher = new SHA1CryptoServiceProvider())
			{
				byte[] hashBytes;

				hashBytes = oSHA1Hasher.ComputeHash(bytes);

				string hash = System.BitConverter.ToString(hashBytes);
				hash = hash.Replace("-", "");
				return hash;
			}
		}
    }
}
