using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.CartModule.Core;
using VirtoCommerce.CartModule.Core.Model;
using VirtoCommerce.CartModule.Core.Model.Search;
using VirtoCommerce.CartModule.Core.Services;
using VirtoCommerce.CartModule.Data.Repositories;
using VirtoCommerce.PaymentModule.Core.Model;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.GenericCrud;
using VirtoCommerce.ShippingModule.Core.Model;

namespace VirtoCommerce.CartModule.Web.Controllers.Api
{
    [Route("api/carts")]
    [Authorize]
    public class CartModuleController : Controller
    {
        private readonly ICrudService<ShoppingCart> _shoppingCartService;
        private readonly ISearchService<ShoppingCartSearchCriteria, ShoppingCartSearchResult, ShoppingCart> _searchService;
        private readonly IShoppingCartBuilder _cartBuilder;
        private readonly IShoppingCartTotalsCalculator _cartTotalsCalculator;
        private readonly Func<ICartRepository> _repositoryFactory;

        public CartModuleController(ICrudService<ShoppingCart> shoppingCartService,
            ISearchService<ShoppingCartSearchCriteria, ShoppingCartSearchResult, ShoppingCart> searchService,
            IShoppingCartBuilder cartBuilder,
            IShoppingCartTotalsCalculator cartTotalsCalculator,
            Func<ICartRepository> repositoryFactory)
        {
            _shoppingCartService = shoppingCartService;
            _searchService = searchService;
            _cartBuilder = cartBuilder;
            _cartTotalsCalculator = cartTotalsCalculator;
            _repositoryFactory = repositoryFactory;
        }

        [HttpGet]
        [Route("{storeId}/{customerId}/{cartName}/{currency}/{cultureName}/current")]
        public async Task<ActionResult<ShoppingCart>> GetCart(string storeId, string customerId, string cartName, string currency, string cultureName)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), string.Join(":", storeId, customerId, cartName, currency))).LockAsync())
            {
                await _cartBuilder.GetOrCreateCartAsync(storeId, customerId, cartName, currency, cultureName);
            }
            return Ok(_cartBuilder.Cart);
        }

        [HttpGet]
        [Route("{cartId}/itemscount")]
        public async Task<ActionResult<int>> GetCartItemsCount(string cartId)
        {
            var carts = await _shoppingCartService.GetByIdsAsync(new[] { cartId }, CartResponseGroup.WithLineItems.ToString());
            var cart = carts.FirstOrDefault();
            return Ok(cart?.Items?.Count ?? 0);
        }

        [HttpPost]
        [Route("{cartId}/items")]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> AddItemToCart(string cartId, [FromBody] LineItem lineItem)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Full.ToString());
                await _cartBuilder.TakeCart(cart).AddItem(lineItem).SaveAsync();
            }
            return NoContent();
        }

        [HttpPut]
        [Route("{cartId}/items")]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> ChangeCartItem(string cartId, [FromQuery] string lineItemId, [FromQuery] int quantity)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Full.ToString());
                _cartBuilder.TakeCart(cart);
                var lineItem = _cartBuilder.Cart.Items.FirstOrDefault(i => i.Id == lineItemId);
                if (lineItem != null)
                {
                    await _cartBuilder.ChangeItemQuantity(lineItemId, quantity).SaveAsync();
                }
            }

            return NoContent();
        }

        [HttpDelete]
        [Route("{cartId}/items/{lineItemId}")]
        public async Task<ActionResult<int>> RemoveCartItem(string cartId, string lineItemId)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Full.ToString());
                await _cartBuilder.TakeCart(cart).RemoveItem(lineItemId).SaveAsync();
            }
            return Ok(_cartBuilder.Cart.Items.Count);
        }

        [HttpDelete]
        [Route("{cartId}/items")]
        public async Task<ActionResult> ClearCart(string cartId)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Full.ToString());
                await _cartBuilder.TakeCart(cart).Clear().SaveAsync();
            }
            return NoContent();
        }

        [HttpPatch]
        [Route("{cartId}")]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> MergeWithCart(string cartId, [FromBody] ShoppingCart otherCart)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Full.ToString());
                var builder = await _cartBuilder.TakeCart(cart).MergeWithCartAsync(otherCart);
                await builder.SaveAsync();
            }
            return NoContent();
        }

        [HttpGet]
        [Route("{cartId}/availshippingrates")]
        public async Task<ActionResult<ICollection<ShippingRate>>> GetAvailableShippingRates(string cartId)
        {
            var cart = await _shoppingCartService.GetByIdAsync(cartId, (CartResponseGroup.WithShipments | CartResponseGroup.WithLineItems).ToString());
            var builder = _cartBuilder.TakeCart(cart);
            var shippingRates = await builder.GetAvailableShippingRatesAsync();
            return Ok(shippingRates);
        }

        [HttpPost]
        [Route("availshippingrates")]
        public async Task<ActionResult<ICollection<ShippingRate>>> GetAvailableShippingRatesByContext([FromBody] ShippingEvaluationContext context)
        {
            var builder = _cartBuilder.TakeCart(context.ShoppingCart);
            var shippingRates = await builder.GetAvailableShippingRatesAsync();
            return Ok(shippingRates);
        }

        [HttpGet]
        [Route("{cartId}/availpaymentmethods")]
        public async Task<ActionResult<ICollection<PaymentMethod>>> GetAvailablePaymentMethods(string cartId)
        {
            var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.WithPayments.ToString());
            var paymentMethods = await _cartBuilder.TakeCart(cart).GetAvailablePaymentMethodsAsync();
            return Ok(paymentMethods);
        }

        [HttpPost]
        [Route("{cartId}/coupons/{couponCode}")]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> AddCartCoupon(string cartId, string couponCode)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Default.ToString());
                await _cartBuilder.TakeCart(cart).AddCoupon(couponCode).SaveAsync();
            }
            return NoContent();
        }

        [HttpDelete]
        [Route("{cartId}/coupons/{couponCode}")]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> RemoveCartCoupon(string cartId, string couponCode)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Default.ToString());
                await _cartBuilder.TakeCart(cart).RemoveCoupon().SaveAsync();
            }
            return NoContent();
        }

        [HttpPost]
        [Route("{cartId}/shipments")]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> AddOrUpdateCartShipment(string cartId, [FromBody] Shipment shipment)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.WithShipments.ToString());
                await (await _cartBuilder.TakeCart(cart).AddOrUpdateShipmentAsync(shipment)).SaveAsync();
            }
            return NoContent();
        }

        [HttpPost]
        [Route("{cartId}/payments")]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> AddOrUpdateCartPayment(string cartId, [FromBody] Payment payment)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(typeof(ShoppingCart), cartId)).LockAsync())
            {
                var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.WithPayments.ToString());
                await (await _cartBuilder.TakeCart(cart).AddOrUpdatePaymentAsync(payment)).SaveAsync();
            }

            return NoContent();
        }

        /// <summary>
        /// Get shopping cart by id
        /// </summary>
        /// <param name="cartId">Shopping cart id</param>
        [HttpGet]
        [Route("{cartId}")]
        public async Task<ActionResult<ShoppingCart>> GetCartById(string cartId)
        {
            var cart = await _shoppingCartService.GetByIdAsync(cartId, CartResponseGroup.Full.ToString());
            return Ok(cart);
        }

        /// <summary>
        /// Search shopping carts by given criteria
        /// </summary>
        /// <param name="criteria">Shopping cart search criteria</param>
        [HttpPost]
        [Route("search")]
        [Authorize(ModuleConstants.Security.Permissions.Read)]
        public async Task<ActionResult<ShoppingCartSearchResult>> SearchShoppingCart([FromBody] ShoppingCartSearchCriteria criteria)
        {
            var result = await _searchService.SearchAsync(criteria);
            return Ok(result);
        }

        /// <summary>
        /// Create shopping cart
        /// </summary>
        /// <param name="cart">Shopping cart model</param>
        [HttpPost]
        [Route("")]
        [Authorize(ModuleConstants.Security.Permissions.Create)]
        public async Task<ActionResult<ShoppingCart>> Create([FromBody] ShoppingCart cart)
        {
            try
            {
                await _shoppingCartService.SaveChangesAsync(new[] { cart });
            }
            catch (FluentValidation.ValidationException ex)
            {
                return BadRequest(ex.Message);
            }

            return Ok(cart);
        }

        /// <summary>
        /// Update shopping cart
        /// </summary>
        /// <param name="cart">Shopping cart model</param>
        [HttpPut]
        [Route("")]
        [Authorize(ModuleConstants.Security.Permissions.Update)]
        public async Task<ActionResult<ShoppingCart>> UpdateShoppingCart([FromBody] ShoppingCart cart)
        {
            using (await AsyncLock.GetLockByKey(CacheKey.With(cart.GetType(), cart.Id)).LockAsync())
            {
                try
                {
                    await _shoppingCartService.SaveChangesAsync(new[] { cart });
                }
                catch (FluentValidation.ValidationException ex)
                {
                    return BadRequest(ex.Message);
                }
            }
            return Ok(cart);
        }

        /// <summary>
        /// Delete shopping carts by ids
        /// </summary>
        /// <param name="ids">Array of shopping cart ids</param>
        [HttpDelete]
        [Route("")]
        [Authorize(ModuleConstants.Security.Permissions.Delete)]
        [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
        public async Task<ActionResult> DeleteCarts([FromQuery] string[] ids)
        {
            //For performance reasons use soft shoping cart deletion synchronously first
            await _shoppingCartService.DeleteAsync(ids, softDelete: true);

            return NoContent();
        }

        /// <summary>
        /// Calculates totals for cart.
        /// </summary>
        /// <param name="cart">Shopping cart model</param>
        [HttpPost]
        [Route("recalculate")]
        public ActionResult<ShoppingCart> RecalculateTotals([FromBody] ShoppingCart cart)
        {
            _cartTotalsCalculator.CalculateTotals(cart);
            return Ok(cart);
        }

    }
}
