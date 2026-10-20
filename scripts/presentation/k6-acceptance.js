// Real 20-user mixed acceptance: isolated test customers/dish, login, browsing,
// one paid checkout per user plus idempotent replay, then order tracking.
// PASSWORD is supplied through the operator environment; never print tokens.
import http from 'k6/http';
import {check, sleep} from 'k6';
import {Counter, Rate} from 'k6/metrics';
const base = (__ENV.BASE_URL || 'http://127.0.0.1:5000').replace(/\/$/,'');
const vus = Number(__ENV.VUS || 20);
const writes = new Counter('paid_orders_created');
const replay = new Rate('checkout_replay_consistent');
const invariants = new Rate('business_invariants');
export const options = {
  vus, duration: __ENV.DURATION || '120s',
  thresholds: {
    http_req_duration:['p(95)<2000'], http_req_failed:['rate<0.01'], checks:['rate==1'],
    paid_orders_created:[`count==${vus}`], checkout_replay_consistent:['rate==1'], business_invariants:['rate==1'],
    'http_req_duration{operation:checkout}':['p(95)<2000'],
    'http_req_duration{operation:login}':['p(95)<2000'],
    'http_req_duration{operation:menu}':['p(95)<2000'],
    'http_req_duration{operation:tracking}':['p(95)<2000'],
  },
};
function api(method,path,body,token,operation,extra={}) {
  return http.request(method,base+path,body===undefined?null:JSON.stringify(body),{
    headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{}),...extra},
    tags:{operation}, timeout:'10s',
  });
}
function data(response,status,label) {
  if (!check(response,{[label]:r=>r.status===status})) throw new Error(label+' HTTP '+response.status);
  return response.json().data;
}
function login(username) {
  return data(api('POST','/api/auth/login',{username,password:__ENV.PASSWORD},null,'login'),200,'login').accessToken;
}
export function setup() {
  if (!__ENV.PASSWORD || vus<1 || vus>40) throw new Error('PASSWORD required; VUS must be 1..40');
  if (__ENV.ALLOW_DEMO_WRITES !== 'true') throw new Error('Set ALLOW_DEMO_WRITES=true: this creates labelled test accounts and orders');
  const merchant=login('merchant1');
  const run=Date.now();
  const stock=vus+5;
  const dish=data(api('POST','/api/merchant/dishes',{name:'Acceptance '+run,category:'Test',price:1,inventory:stock},merchant,'setup'),201,'create test dish');
  const buyers=[];
  for(let i=1;i<=vus;i++) {
    const username=`accept_${run}_${i}`;
    data(api('POST','/api/auth/customer/register',{username,password:__ENV.PASSWORD,displayName:'Test customer '+i,phoneNumber:'80000000'},null,'setup'),201,'register test customer');
    const token=login(username);
    const address=data(api('POST','/api/addresses',{address:'Synthetic test address ZoneA',contactName:'Test customer',phoneNumber:'80000000'},token,'setup'),201,'create test address');
    buyers.push({username,token,addressId:address.id});
  }
  return {run,dishId:dish.id,stock,buyers};
}
let myOrderId;
export default function(test) {
  const buyer=test.buyers[__VU-1];
  if (__ITER===0) {
    data(api('POST','/api/cart/items',{dishId:test.dishId,quantity:1},buyer.token,'cart'),201,'add cart');
    const key=`accept-${test.run}-${__VU}`;
    const body={addressId:buyer.addressId,needUtensils:0,paymentMethod:'SimulatedCard'};
    const paid=data(api('POST','/api/checkout',body,buyer.token,'checkout',{'Idempotency-Key':key}),201,'paid checkout');
    myOrderId=paid.orderId; writes.add(1);
    const repeated=data(api('POST','/api/checkout',body,buyer.token,'checkout',{'Idempotency-Key':key}),200,'replay checkout');
    replay.add(repeated.orderId===myOrderId && repeated.replayed===true);
  }
  data(api('GET','/api/merchants/1/dishes',undefined,null,'menu'),200,'read menu');
  const orders=data(api('GET','/api/orders',undefined,buyer.token,'tracking'),200,'track orders');
  invariants.add(orders.length===1 && orders[0].id===myOrderId);
  if(__ITER%10===0) login(buyer.username);
  sleep(0.5);
}
export function teardown(test) {
  const dish=data(api('GET','/api/catalog/dishes/'+test.dishId,undefined,null,'verification'),200,'verify stock');
  invariants.add(dish.inventory===test.stock-vus && dish.inventory>=0);
  console.log(JSON.stringify({testRun:test.run,testDishId:test.dishId,initialStock:test.stock,finalStock:dish.inventory,expectedPaidOrders:vus}));
}
export function handleSummary(summary) {
  return {'acceptance-summary.json':JSON.stringify(summary,null,2)};
}
