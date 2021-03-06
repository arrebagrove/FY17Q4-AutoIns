﻿using ContosoInsurance.Common;
using ContosoInsurance.Common.Data.CRM;
using ContosoInsurance.MVC.Helper;
using ContosoInsurance.MVC.Models;
using ContosoInsurance.MVC.Utils;
using Newtonsoft.Json;
using System;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Mvc;

namespace ContosoInsurance.MVC.Controllers
{
    public class ClaimsController : Controller
    {
        private ClaimsDbContext dbContext;

        public ClaimsController()
        {
            dbContext = new ClaimsDbContext();
        }

        // GET: Search
        public ActionResult Index()
        {
            return View();
        }

        [ApplicationInsights("Data queried from CRM Claims SQL database", IdParamName = "claimId")]
        [HttpGet, Route("search")]
        public async Task<JsonResult> Search(int? claimId, string policyHolderId, string firstName, string lastName)
        {
            var queryable = dbContext.Claims
                    .WhereIf(i => i.Id == claimId, claimId.HasValue)
                    .WhereIf(i => i.Vehicle.Customer.PolicyId == policyHolderId,policyHolderId.IsNotNullAndEmpty())
                    .WhereIf(i => i.Vehicle.Customer.FirstName.Contains(firstName), firstName.IsNotNullAndEmpty())
                    .WhereIf(i => i.Vehicle.Customer.LastName.Contains(lastName), lastName.IsNotNullAndEmpty())
                    .Select(i => new
                    {
                        claimId = i.Id,
                        firstName = i.Vehicle.Customer.FirstName,
                        lastName = i.Vehicle.Customer.LastName,
                        claimType = i.Type,
                        dueDate = i.DateTime,
                        claimStatus = i.Status.ToString(),
                        damageAssessment = i.DamageAssessment.ToString()
                    }).OrderBy(i => i.claimId);
            var result = await queryable.ToArrayAsync();
            return ToJson(result);
        }

        [HttpGet, Route("details/{id}")]
        public async Task<ActionResult> Details(int? id)
        {
            if (id == null) return RedirectToAction("Index");

            var queryable = dbContext.Claims.Include(i => i.Vehicle.Customer)
                                        .Where(i => i.Id == id);
            var claim = await queryable.FirstOrDefaultAsync();
            if (claim == null) return HttpNotFound();


            var vehicle = claim.Vehicle;
            var customer = vehicle.Customer;
            var otherParty = claim.OtherParty;

            var queryClaims = dbContext.Claims.Where(i => i.Vehicle.CustomerId == customer.Id)
                                              .Select(i => new
                                              {
                                                  claimId = i.Id,
                                                  firstName = i.Vehicle.Customer.FirstName,
                                                  lastName = i.Vehicle.Customer.LastName,
                                                  claimType = i.Type,
                                                  dueDate = i.DateTime,
                                                  claimStatus = i.Status.ToString(),
                                                  damageAssessment = i.DamageAssessment.ToString()
                                              });

            var claimHistory = await queryClaims.ToListAsync();
            var result = new
            {
                claimId = claim.Id,
                correlationId = claim.CorrelationId,
                dateTime = claim.DateTime,
                dueDate = claim.DueDate,
                status = claim.Status.ToString(),
                damageAssessment = claim.DamageAssessment.ToString(),
                location = new
                {
                    longitude = claim.Coordinates?.Longitude,
                    latitude = claim.Coordinates?.Latitude,
                },
                description = claim.Description,
                customer = new
                {
                    name = customer.FirstName + ' ' + customer.LastName,
                    street = customer.Street,
                    city = customer.City,
                    state = customer.State,
                    zip = customer.Zip,
                    dob = customer.DOB,
                    phone = customer.MobilePhone,
                    email = customer.Email,
                    policyId = customer.PolicyId,
                    policyStart = customer.PolicyStart,
                    driversLicenseNumber = customer.DriversLicenseNumber,
                },
                vehicle = new
                {
                    vehicleNumber = vehicle.VIN,
                    licensePlate = vehicle.LicensePlate
                },
                otherParty = new
                {
                    name = otherParty.FirstName + " " + otherParty.LastName,
                    street = otherParty.Street,
                    city = otherParty.City,
                    state = otherParty.State,
                    zip = otherParty.Zip,
                    dob = otherParty.DOB,
                    phone = otherParty.MobilePhone,
                    email = "",
                    licensePlate = otherParty.LicensePlate,
                    policyId = otherParty.PolicyId,
                    vehicleNumber = otherParty.VIN,
                    driversLicenseNumber = otherParty.DriversLicenseNumber,
                    licensePlateImageURL = BlobUtil.ConverToBlobSas(otherParty.LicensePlateImageUrl),
                    insuranceCardImageURL = BlobUtil.ConverToBlobSas(otherParty.InsuranceCardImageUrl),
                    driversLicenseImageURL = BlobUtil.ConverToBlobSas(otherParty.DriversLicenseImageUrl)
                },
                images = claim.Images.Select(i => BlobUtil.ConverToBlobSas(i.ImageUrl)).ToArray(),
                claimHisotry = claimHistory
            };

            ViewData["claimData"] = JsonConvert.SerializeObject(result); ;
            return View();
        }

        private JsonResult ToJson(object obj)
        {
            return Json(obj, JsonRequestBehavior.AllowGet);
        }

        [ApplicationInsights("Invoked ContosoClaimManualApprover Azure Function", IdParamName = "cid")]
        [HttpPost, Route("approve")]
        public async Task<ActionResult> Approve(int id,bool approved, string damageAssessment,string cid)
        {
            var queryable = dbContext.Claims.Include(i => i.Vehicle.Customer)
                                         .Where(i => i.Id == id);
            var claim = await queryable.FirstOrDefaultAsync();
            var customer = claim.Vehicle.Customer;
            var url = Common.AppSettings.ClaimManualApproverUrl;
            var response = await PostTo(url, new {
                    id = id,
                    correlationId = claim.CorrelationId,
                    damageAssessment = ClaimUtil.ParseDamageAssessmentToInt(damageAssessment),
                    approved = approved,
                    customerUserId = Regex.Match(customer.UserId, @"sid:\w+").Value,
                    customerName = customer.FirstName + " " + customer.LastName,
                    customerEmail = customer.Email
            });
            return Json(response);
        }

        private static async Task<HttpResponseMessage> PostTo(string url, string content, string mediaType = "text/plain")
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Content = new StringContent(content, Encoding.UTF8, mediaType);

            var client = new HttpClient();
            var responseMessage = await client.SendAsync(requestMessage);
            responseMessage.EnsureSuccessStatusCode();
            return responseMessage;
        }

        private static Task<HttpResponseMessage> PostTo(string url, object obj)
        {
            var content = JsonConvert.SerializeObject(obj);
            return PostTo(url, content, "application/json");
        }

        // GET: Create Claim 
        public async Task<ActionResult> AddClaim()
        {
            var customer = await ClaimUtil.GetCustomerMock();
            ViewBag.MockInfo = new AddClaimViewModel
            {
                CustomerId = customer.Id,
                CustomerVehicleId = customer.Vehicles.First().Id,
                UserPhotoPath = AppSettings.CustomerMock.UserPhotoPath,
                DriverLicensePath = AppSettings.CustomerMock.DriverLicensePath,
                LicensePlatePath = AppSettings.CustomerMock.LicensePlatePath,
                PolicyIDPath = AppSettings.CustomerMock.PolicyIDPath,
                CrashImagesPaths = AppSettings.CustomerMock.CrashImagesPaths,
                SelectedVehiclePath = AppSettings.CustomerMock.SelectedVehiclePath
                 
            };
            return View();
        }

        [ApplicationInsights("Invoked ContosoClaimManualApprover Azure Function", IdParamName = "cid")]
        [HttpPost, Route("addClaim")]
        public async Task<ActionResult> AddClaim(string phonenumber,string description)
        {
            var newGuid = Guid.NewGuid();
            var otherPartyImages = ClaimUtil.GetOtherPartyImages();
            var claimImages = ClaimUtil.GetClaimImages(newGuid);
            var customer = await ClaimUtil.GetCustomerMock();
            var vehicle = customer.Vehicles.First();
            var postImages = new
            {
                otherPartyImages = otherPartyImages.Select(otherImg => new { plateJson = otherImg.Item1, kind = otherImg.Item2}).ToArray(),
                claimImages = claimImages.Select(claimImg => new { plateJson = claimImg.Item1, imgName= claimImg.Item2}).ToArray(),
            };
            var response = await PostTo(AppSettings.CreateMobileClaimFromWebUrl, new {
                claimId = newGuid.ToString(),
                description=description??string.Empty,
                phonenumber = phonenumber??string.Empty,
                userId=customer.UserId,
                vehicleId = vehicle.Id,
                postImages = postImages
            });
            return Json(new { success=true});
        }

    }
}