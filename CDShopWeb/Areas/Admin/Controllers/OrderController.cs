﻿using CDShop.DataAccess.Repository.IRepository;
using CDShop.Models;
using CDShop.Models.ViewModels;
using CDShop.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using Stripe.Checkout;
using System.Security.Claims;

namespace CDShopWeb.Areas.Admin.Controllers
{
	[Area("Admin")]
    [Authorize]
	public class OrderController : Controller
	{
		private readonly IUnitOfWork _unitOfWork;
        [BindProperty]
        public OrderVM OrderVM { get; set; }

        public OrderController(IUnitOfWork unitOfWork)
		{
			_unitOfWork = unitOfWork;
		}

		public IActionResult Index()
		{
			return View();
		}

        public IActionResult Details(int orderId)
        {
            OrderVM = new OrderVM()
            {
                OrderHeader = _unitOfWork.OrderHeader.GetFirstOrDefault(u=>u.Id== orderId, includeProperties:"ApplicationUser"),
                OrderDetail = _unitOfWork.OrderDetail.GetAll(u=>u.OrderId== orderId, includeProperties:"Product")
            };

            return View(OrderVM);
        }

		[ActionName("Details")]
		[HttpPost]
		[ValidateAntiForgeryToken]
		public IActionResult DetailsPOST()
		{
			OrderVM.OrderHeader = _unitOfWork.OrderHeader.GetFirstOrDefault(u => u.Id == OrderVM.OrderHeader.Id, includeProperties: "ApplicationUser");
			OrderVM.OrderDetail = _unitOfWork.OrderDetail.GetAll(u => u.OrderId == OrderVM.OrderHeader.Id, includeProperties: "Product");

			var domain = "https://localhost:44392/";
			var options = new SessionCreateOptions
			{
				LineItems = new List<SessionLineItemOptions>(),
				Mode = "payment",
				SuccessUrl = domain + $"admin/order/PaymentConfirmation?orderHeaderId={OrderVM.OrderHeader.Id}",
				CancelUrl = domain + $"admin/order/details?orderId={OrderVM.OrderHeader.Id}",
			};

			foreach (var item in OrderVM.OrderDetail)
			{
				var sessionLineItem = new SessionLineItemOptions
				{
					PriceData = new SessionLineItemPriceDataOptions
					{
						UnitAmount = (long)(item.Price * 100),
						Currency = "pln",
						ProductData = new SessionLineItemPriceDataProductDataOptions
						{
							Name = item.Product.Title,
						},
					},
					Quantity = item.Count,
				};
				options.LineItems.Add(sessionLineItem);
			}

			var service = new SessionService();
			Session session = service.Create(options);
			_unitOfWork.OrderHeader.UpdateStripePaymentId(OrderVM.OrderHeader.Id,
				session.Id, session.PaymentIntentId);
			_unitOfWork.Save();
			Response.Headers.Add("Location", session.Url);
			return new StatusCodeResult(303);
		}

		public IActionResult PaymentConfirmation(int orderHeaderId)
		{
			OrderHeader orderHeader = _unitOfWork.OrderHeader.GetFirstOrDefault(u => u.Id == orderHeaderId);
			if (orderHeader.PaymentStatus == SD.PaymentStatusDelayedPayment)
			{
				var service = new SessionService();
				Session session = service.Get(orderHeader.SessionId);
				if (session.PaymentStatus.ToLower() == "paid")
				{
					_unitOfWork.OrderHeader.UpdateStripePaymentId(orderHeaderId, orderHeader.SessionId, session.PaymentIntentId);
					_unitOfWork.OrderHeader.UpdateStatus(orderHeaderId, orderHeader.OrderStatus, SD.PaymentStatusApproved);
					_unitOfWork.Save();
				}
			}


			return View(orderHeaderId);
		}

		[HttpPost]
		[Authorize(Roles = SD.Role_Admin + "," + SD.Role_Employee)]
		[ValidateAntiForgeryToken]
		public IActionResult UpdateOrderDetail()
		{
            var orderHeaderFromDb = _unitOfWork.OrderHeader.GetFirstOrDefault(u => u.Id == OrderVM.OrderHeader.Id);

			orderHeaderFromDb.Name = OrderVM.OrderHeader.Name;
			orderHeaderFromDb.PhoneNumber = OrderVM.OrderHeader.PhoneNumber;
			orderHeaderFromDb.City = OrderVM.OrderHeader.City;
			orderHeaderFromDb.StreetAddress = OrderVM.OrderHeader.StreetAddress;
			orderHeaderFromDb.State = OrderVM.OrderHeader.State;
			orderHeaderFromDb.PostalCode = OrderVM.OrderHeader.PostalCode;

            if(OrderVM.OrderHeader.Carrier != null)
            {
				orderHeaderFromDb.Carrier = OrderVM.OrderHeader.Carrier;
			}
			if (OrderVM.OrderHeader.TrackingNumber != null)
			{
				orderHeaderFromDb.TrackingNumber = OrderVM.OrderHeader.TrackingNumber;
			}

            _unitOfWork.OrderHeader.Update(orderHeaderFromDb);
            _unitOfWork.Save();

			return RedirectToAction("Details", "Order", new {orderID =orderHeaderFromDb.Id});
		}

        [HttpPost]
		[Authorize(Roles = SD.Role_Admin + "," + SD.Role_Employee)]
		[ValidateAntiForgeryToken]
		public IActionResult StartProcessing()
		{
            _unitOfWork.OrderHeader.UpdateStatus(OrderVM.OrderHeader.Id, SD.StatusInProcess);
            _unitOfWork.Save();

			return RedirectToAction("Details", "Order", new {orderID = OrderVM.OrderHeader.Id });
		}

		[HttpPost]
		[Authorize(Roles = SD.Role_Admin + "," + SD.Role_Employee)]
		[ValidateAntiForgeryToken]
		public IActionResult ShipOrder()
		{
			var orderHeader = _unitOfWork.OrderHeader.GetFirstOrDefault(u => u.Id == OrderVM.OrderHeader.Id);
            orderHeader.TrackingNumber = OrderVM.OrderHeader.TrackingNumber;
            orderHeader.Carrier = OrderVM.OrderHeader.Carrier;
			orderHeader.OrderStatus = SD.StatusShipped;
            orderHeader.ShippingDate = DateTime.Now;

			if (orderHeader.PaymentStatus == SD.PaymentStatusDelayedPayment)
			{
				orderHeader.PaymentDueDate = DateTime.Now.AddDays(30);
			}

			_unitOfWork.OrderHeader.Update(orderHeader);
			_unitOfWork.Save();

			return RedirectToAction("Details", "Order", new { orderID = OrderVM.OrderHeader.Id });
		}

		[HttpPost]
		[Authorize(Roles = SD.Role_Admin + "," + SD.Role_Employee)]
		[ValidateAntiForgeryToken]
		public IActionResult CancelOrder()
		{
			var orderHeader = _unitOfWork.OrderHeader.GetFirstOrDefault(u => u.Id == OrderVM.OrderHeader.Id);
			
			if(orderHeader.PaymentStatus == SD.PaymentStatusApproved)
			{
				var options = new RefundCreateOptions
				{
					Reason = RefundReasons.RequestedByCustomer,
					PaymentIntent = orderHeader.PaymentIntentId
				};
				var service = new RefundService();
				Refund refund = service.Create(options);
				_unitOfWork.OrderHeader.UpdateStatus(orderHeader.Id, SD.StatusCancelled, SD.StatusRefunded);
			}
			else
			{
				_unitOfWork.OrderHeader.UpdateStatus(orderHeader.Id, SD.StatusCancelled, SD.StatusRefunded);
			}

			_unitOfWork.Save();

			return RedirectToAction("Details", "Order", new { orderID = OrderVM.OrderHeader.Id });
		}
		#region API CALLS
		[HttpGet]
		public IActionResult GetAll(string status)
		{
            IEnumerable<OrderHeader> orderHeaders;

            if(User.IsInRole(SD.Role_Admin) || User.IsInRole(SD.Role_Employee))
            {
                orderHeaders = _unitOfWork.OrderHeader.GetAll(includeProperties: "ApplicationUser");
            }
            else
            {
                var claimIsdentity = (ClaimsIdentity)User.Identity;
                var claim = claimIsdentity.FindFirst(ClaimTypes.NameIdentifier);

                orderHeaders = _unitOfWork.OrderHeader.GetAll(u=>u.ApplicationUserId == claim.Value, includeProperties: "ApplicationUser");
            }
            

            switch (status)
            {
                case "pending":
                    orderHeaders = orderHeaders.Where(u => u.PaymentStatus == SD.PaymentStatusDelayedPayment);
                    break;
                case "completed":
                    orderHeaders = orderHeaders.Where(u => u.OrderStatus == SD.StatusInProcess);
                    break;
                case "inprocess":
                    orderHeaders = orderHeaders.Where(u => u.OrderStatus == SD.StatusShipped);
                    break;
                case "approved":
                    orderHeaders = orderHeaders.Where(u => u.OrderStatus == SD.StatusApproved);
                    break;
                default:
                    break;
            }
            return Json(new { data = orderHeaders });
        }
		#endregion
	}
}
