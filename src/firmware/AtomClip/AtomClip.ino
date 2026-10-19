// AtomClip v2: native USB bridge with USB-provisioned device identity and authenticated BLE.
#include <Arduino.h>
#include <USB.h>
#include <USBHIDKeyboard.h>
#include <NimBLEDevice.h>
#include <atomic>
#include "DeviceConfig.h"
#include "SecureSelfTest.h"
#include "Typing.h"

static constexpr char SERVICE[]="a29bc201-12a5-4ea1-81a0-2c4dd9cd0001";
static constexpr char INFO_UUID[]="a29bc202-12a5-4ea1-81a0-2c4dd9cd0001";
static constexpr char AUTH_UUID[]="a29bc203-12a5-4ea1-81a0-2c4dd9cd0001";
static constexpr char RX_UUID[]="a29bc204-12a5-4ea1-81a0-2c4dd9cd0001";
static constexpr char TX_UUID[]="a29bc205-12a5-4ea1-81a0-2c4dd9cd0001";
static NimBLEServer* server;
static NimBLECharacteristic *tx,*authChar,*infoChar;
static QueueHandle_t toUsb,toBle,toAuth;
static SemaphoreHandle_t secureLock;
static ac::Parser usbParser(true);
static secure::Stream authStream(true),sessionStream(false);
static secure::Session session;
static ac::Bytes handshakeInfo;
static uint8_t clientNonce[32]{},serverNonce[32]{};
static unsigned authStage=0;
static std::atomic<bool> bleConnected{false},subscribed{false},authSubscribed{false},authenticated{false},disconnectRequested{false};
static std::atomic<uint16_t> connection{BLE_HS_CONN_HANDLE_NONE};
static std::atomic<uint32_t> usbSeen{0},bleSeen{0},lastFragment{0},connectedAt{0},authEpoch{0},identifyUntil{0};
static std::atomic<bool> haveUsb{false},haveBle{false};

static void enqueue(QueueHandle_t q,ac::Bytes&& b){auto* p=new(std::nothrow) ac::Bytes(std::move(b));if(p&&xQueueSend(q,&p,0)!=pdTRUE)delete p;}
static void drain(QueueHandle_t q){ac::Bytes* p;while(xQueueReceive(q,&p,0)==pdTRUE)delete p;}
struct Outgoing {
 ac::Bytes* data=nullptr;size_t offset=0;uint32_t started=0;
 void clear(){delete data;data=nullptr;offset=0;}
 bool next(QueueHandle_t q,uint32_t now){if(!data&&xQueueReceive(q,&data,0)==pdTRUE){offset=0;started=now;}return data!=nullptr;}
};
static Outgoing usbOut,bleOut,authOut;
static bool authorized(){return authenticated.load()&&bleConnected.load()&&subscribed.load();}
static uint32_t currentEpoch(){return authEpoch.load();}
static bool usbAppOnline(){return haveUsb&&uint32_t(millis()-usbSeen.load())<15000;}
// All accesses to session/handshake, BLE outgoing queues and resets hold secureLock.
static void resetSecure(){
 authenticated=false;authEpoch++;haveBle=false;authStage=0;session.reset();authStream.reset();sessionStream.reset();
 mbedtls_platform_zeroize(clientNonce,32);mbedtls_platform_zeroize(serverNonce,32);handshakeInfo.clear();
 drain(toBle);drain(toAuth);bleOut.clear();authOut.clear();
 // A BLE connection must not drop physical-USB provisioning acknowledgements.
 ac::Bytes* item;ac::Bytes* keep[2]{};unsigned kept=0;while(xQueueReceive(toUsb,&item,0)==pdTRUE){if(item->at(4)==7&&kept<2)keep[kept++]=item;else delete item;}for(unsigned i=0;i<kept;i++)if(xQueueSend(toUsb,&keep[i],0)!=pdTRUE)delete keep[i];
 if(usbOut.data&&usbOut.data->at(4)!=7)usbOut.clear();identifyUntil=0;
}
static void failSession(){authenticated=false;authStage=3;authEpoch++;session.reset();disconnectRequested=true;}
static void status(bool usb){
 uint32_t now=millis();uint32_t caps=board::capabilities;
 uint8_t flags[9]={uint8_t(haveUsb&&uint32_t(now-usbSeen.load())<15000),uint8_t(authorized()&&haveBle&&uint32_t(now-bleSeen.load())<15000),
 uint8_t((caps&4)!=0),uint8_t((caps&4)!=0),uint8_t((caps&2)!=0),uint8_t((caps&4)!=0),uint8_t((caps&4)!=0),uint8_t((caps&16)!=0),uint8_t((caps&8)!=0)};
 if(usb)enqueue(toUsb,ac::frame(4,flags,9));else if(authorized())enqueue(toBle,ac::frame(4,flags,9));
}
static void typeResult(const uint8_t* id,uint8_t result,uint32_t epoch){xSemaphoreTake(secureLock,portMAX_DELAY);if(authorized()&&epoch==authEpoch.load())enqueue(toBle,typing::resultFrame(id,result));xSemaphoreGive(secureLock);}
static bool validInner(const ac::Bytes& b){return b.size()>=13&&b.size()<=secure::MAX_INNER&&memcmp(b.data(),"ACB1",4)==0&&ac::le32(b.data()+5)==b.size()-13&&ac::crc(b.data()+4,b.size()-8)==ac::le32(b.data()+b.size()-4);}
static bool acceptBle(ac::Bytes&& b){
 if(!authorized()||!validInner(b))return false;uint8_t kind=b[4];bleSeen=millis();haveBle=true;
 if(kind==3){status(false);return true;}
 if(kind==8){if(b.size()!=14||b[9]!=1)return false;uint8_t result=(board::rgbLedPin>=0&&(board::capabilities&32))?0:1;if(result==0)identifyUntil=millis()+2500;enqueue(toBle,ac::frame(8,&result,1));return true;}
 if(kind==5){if(!(board::capabilities&2))return false;typing::submit(std::move(b),device::dataKey,authEpoch.load());return true;}
 // Management type7 is USB-only and must never be routed or answered over BLE.
 if(kind!=1&&kind!=2)return false;
 if(usbAppOnline())enqueue(toUsb,std::move(b));return true;
}
static void usbReply(uint8_t op,const uint8_t* request,uint8_t code,const ac::Bytes& result){ac::Bytes p(18);p[0]=op|0x80;memcpy(p.data()+1,request,16);p[17]=code;secure::append(p,result.data(),result.size());enqueue(toUsb,ac::frame(7,p.data(),p.size()));}
static void usbManagement(const ac::Bytes& frame){
 size_t n=frame.size()-13;const uint8_t* p=frame.data()+9;if(n<17||n>82)return;uint8_t op=p[0];const uint8_t* request=p+1;ac::Bytes result;uint8_t error=0;
 xSemaphoreTake(secureLock,portMAX_DELAY);
 if(op==1&&n==17)result=device::info();
 else if(op==4&&n==17){result=device::info();secure::append(result,device::dataKey,32);}
 else if(op==5&&n==17){if(!device::configured)error=3;else{result=device::info();ac::Bytes message;secure::domain(message,"AtomClip/v2/usb-proof");secure::append(message,result.data(),40);secure::append(message,request,16);uint8_t proof[32];if(!secure::mac(device::controlKey,message.data(),message.size(),proof))error=4;else secure::append(result,proof,32);mbedtls_platform_zeroize(proof,32);}}
 else if(op==2&&n==82&&(p[17]&~1)==0){
  if(device::configured&&!(p[17]&1))error=2;
  else {resetSecure();disconnectRequested=bleConnected.load();if(!device::provision(p+18,p+50,(p[17]&1)!=0))error=4;else{result=device::info();ac::Bytes message;secure::domain(message,"AtomClip/v2/usb-bind");secure::append(message,result.data(),40);secure::append(message,request,16);uint8_t proof[32];if(!secure::mac(device::controlKey,message.data(),message.size(),proof))error=4;else secure::append(result,proof,32);mbedtls_platform_zeroize(proof,32);}}
 }
 else if(op==3&&n==17){resetSecure();disconnectRequested=bleConnected.load();if(!device::reset())error=4;else result=device::info();}
 else error=1;
 if(error)result.clear();usbReply(op,request,error,result);if(infoChar){ac::Bytes info=device::info();infoChar->setValue(info.data(),info.size());}
 xSemaphoreGive(secureLock);
}
static void acceptUsb(ac::Bytes&& b){
 if(b[4]==7){usbManagement(b);return;}usbSeen=millis();haveUsb=true;
 xSemaphoreTake(secureLock,portMAX_DELAY);if(b[4]==3)status(true);else if((b[4]==1||b[4]==2)&&authorized())enqueue(toBle,std::move(b));xSemaphoreGive(secureLock);
}
static bool authMessage(ac::Bytes&& frame){
 if(authStage==0){
  if(!device::configured||!subscribed||!authSubscribed||!secure::validControl(frame,1,48)||!secure::equal(frame.data()+7,device::id,16))return false;
  memcpy(clientNonce,frame.data()+23,32);esp_fill_random(serverNonce,32);handshakeInfo=device::info();uint8_t proof[32];if(!secure::proof(device::controlKey,"AtomClip/v2/server",handshakeInfo.data(),clientNonce,serverNonce,proof))return false;
  ac::Bytes payload=handshakeInfo;secure::append(payload,serverNonce,32);secure::append(payload,proof,32);enqueue(toAuth,secure::control(2,payload.data(),payload.size()));mbedtls_platform_zeroize(proof,32);authStage=1;return true;
 }
 if(authStage==1){
  if(!secure::validControl(frame,3,32))return false;uint8_t expected[32];bool ok=secure::proof(device::controlKey,"AtomClip/v2/client",handshakeInfo.data(),clientNonce,serverNonce,expected)&&secure::equal(frame.data()+7,expected,32);mbedtls_platform_zeroize(expected,32);if(!ok)return false;
  if(!session.derive(device::controlKey,handshakeInfo.data(),clientNonce,serverNonce))return false;
  uint8_t finish[32];if(!secure::proof(device::controlKey,"AtomClip/v2/finish",handshakeInfo.data(),clientNonce,serverNonce,finish))return false;
  enqueue(toAuth,secure::control(4,finish,32));mbedtls_platform_zeroize(finish,32);authStage=2;authenticated=true;lastFragment=millis();return true;
 }
 return false;
}
class ServerCallbacks:public NimBLEServerCallbacks {
 void onConnect(NimBLEServer* s,NimBLEConnInfo& info)override{xSemaphoreTake(secureLock,portMAX_DELAY);resetSecure();connection=info.getConnHandle();bleConnected=true;subscribed=false;authSubscribed=false;disconnectRequested=false;connectedAt=millis();xSemaphoreGive(secureLock);s->updateConnParams(info.getConnHandle(),12,24,0,300);}
 void onDisconnect(NimBLEServer*,NimBLEConnInfo&,int)override{xSemaphoreTake(secureLock,portMAX_DELAY);bleConnected=false;subscribed=false;authSubscribed=false;connection=BLE_HS_CONN_HANDLE_NONE;resetSecure();xSemaphoreGive(secureLock);NimBLEDevice::startAdvertising();}
} serverCallbacks;
class InfoCallbacks:public NimBLECharacteristicCallbacks {
 void onRead(NimBLECharacteristic* c,NimBLEConnInfo&)override{xSemaphoreTake(secureLock,portMAX_DELAY);ac::Bytes info=device::info();c->setValue(info.data(),info.size());xSemaphoreGive(secureLock);}
} infoCallbacks;
class AuthCallbacks:public NimBLECharacteristicCallbacks {
 void onSubscribe(NimBLECharacteristic*,NimBLEConnInfo&,uint16_t value)override{authSubscribed=(value&1)!=0;if(!authSubscribed&&authenticated)disconnectRequested=true;}
 void onWrite(NimBLECharacteristic* c,NimBLEConnInfo&)override{auto value=c->getValue();xSemaphoreTake(secureLock,portMAX_DELAY);if(!authStream.feed(reinterpret_cast<const uint8_t*>(value.data()),value.size(),millis(),authMessage))failSession();xSemaphoreGive(secureLock);}
} authCallbacks;
class RxCallbacks:public NimBLECharacteristicCallbacks {
 void onWrite(NimBLECharacteristic* c,NimBLEConnInfo&)override{
  auto value=c->getValue();xSemaphoreTake(secureLock,portMAX_DELAY);
  if(!authorized())failSession();else {lastFragment=millis();if(!sessionStream.feed(reinterpret_cast<const uint8_t*>(value.data()),value.size(),millis(),[](ac::Bytes&& encrypted){ac::Bytes plain;return session.open(encrypted,plain)&&acceptBle(std::move(plain));}))failSession();}
  xSemaphoreGive(secureLock);
 }
} rxCallbacks;
class TxCallbacks:public NimBLECharacteristicCallbacks {
 void onSubscribe(NimBLECharacteristic*,NimBLEConnInfo&,uint16_t value)override{subscribed=(value&1)!=0;if(!subscribed&&authenticated)disconnectRequested=true;}
} txCallbacks;

void setup(){
 toUsb=xQueueCreate(2,sizeof(ac::Bytes*));toBle=xQueueCreate(2,sizeof(ac::Bytes*));toAuth=xQueueCreate(2,sizeof(ac::Bytes*));secureLock=xSemaphoreCreateMutex();
 if(!toUsb||!toBle||!toAuth||!secureLock){for(;;)delay(1000);}
 USB.productName("AtomClip v2 Text Bridge");Serial.setRxBufferSize(20000);Serial.setTxTimeoutMs(10);Serial.enableReboot(false);Serial.begin(115200);typing::keyboard.begin();USB.begin();
 if(!device::begin()||!securetest::run()){for(;;)delay(1000);} // Corrupt persistent identity fails closed; never silently reclaims a board.
 char name[8];snprintf(name,sizeof(name),"AC-%02X%02X",device::id[0],device::id[1]);
 NimBLEDevice::init(name);NimBLEDevice::setMTU(185);server=NimBLEDevice::createServer();server->setCallbacks(&serverCallbacks);auto* svc=server->createService(SERVICE);
 infoChar=svc->createCharacteristic(INFO_UUID,NIMBLE_PROPERTY::READ,40);infoChar->setCallbacks(&infoCallbacks);ac::Bytes info=device::info();infoChar->setValue(info.data(),info.size());
 authChar=svc->createCharacteristic(AUTH_UUID,NIMBLE_PROPERTY::WRITE|NIMBLE_PROPERTY::NOTIFY,512);authChar->setCallbacks(&authCallbacks);
 auto* rx=svc->createCharacteristic(RX_UUID,NIMBLE_PROPERTY::WRITE,512);rx->setCallbacks(&rxCallbacks);tx=svc->createCharacteristic(TX_UUID,NIMBLE_PROPERTY::NOTIFY,512);tx->setCallbacks(&txCallbacks);svc->start();
 uint8_t hint[13]{};hint[0]=2;hint[1]=device::id[0];hint[2]=device::id[1];hint[3]=board::id;hint[4]=board::id>>8;ac::put32(hint+5,board::capabilities);hint[9]=board::flashBytes/(1024*1024);hint[10]=0;hint[11]=2;hint[12]=0;
 NimBLEAdvertisementData advertisement,response;bool built=advertisement.setFlags(0x06)&&advertisement.setCompleteServices(NimBLEUUID(SERVICE))&&advertisement.setName(name)&&response.setServiceData(NimBLEUUID(SERVICE),hint,sizeof(hint));
 if(!built||advertisement.getPayload().size()>31||response.getPayload().size()>31){for(;;)delay(1000);}
 auto* adv=NimBLEDevice::getAdvertising();adv->enableScanResponse(true);adv->setAdvertisementData(advertisement);adv->setScanResponseData(response);adv->start();
 typing::begin(usbAppOnline,typeResult,authorized,currentEpoch,board::buttonPin,board::capabilities);
}
void loop(){
 uint32_t now=millis();uint8_t input[256];size_t n=Serial.available();if(n){n=Serial.read(input,std::min(n,sizeof(input)));usbParser.feed(input,n,now,[](ac::Bytes&& b){acceptUsb(std::move(b));});}usbParser.expire(millis());
 xSemaphoreTake(secureLock,portMAX_DELAY);now=millis();
 if(bleConnected&&((!authenticated&&uint32_t(now-connectedAt.load())>15000)||(authenticated&&uint32_t(now-lastFragment.load())>20000)||authStream.expired(now)||sessionStream.expired(now)))failSession();
 if(usbOut.next(toUsb,now)){
  bool control=usbOut.data->at(4)==7,statusFrame=usbOut.data->at(4)==4;
  if(uint32_t(now-usbOut.started)>120000||(!control&&!statusFrame&&(!authorized()||!usbAppOnline())))usbOut.clear();else{size_t count=std::min(size_t(256),usbOut.data->size()-usbOut.offset);usbOut.offset+=Serial.write(usbOut.data->data()+usbOut.offset,count);if(usbOut.offset==usbOut.data->size())usbOut.clear();}
 }
 uint16_t handle=connection.load();size_t mtu=bleConnected?server->getPeerMTU(handle):23;size_t chunk=std::min(size_t(180),mtu>3?mtu-3:size_t(20));
 if(authOut.next(toAuth,now)){
  if(!bleConnected||!authSubscribed||uint32_t(now-authOut.started)>15000){authOut.clear();failSession();}else{size_t count=std::min(chunk,authOut.data->size()-authOut.offset);if(authChar->notify(authOut.data->data()+authOut.offset,count,handle)){authOut.offset+=count;if(authOut.offset==authOut.data->size())authOut.clear();}}
 }else if(authorized()){
  if(!bleOut.data){ac::Bytes* raw=nullptr;if(xQueueReceive(toBle,&raw,0)==pdTRUE){ac::Bytes encoded;if(session.seal(*raw,encoded)){bleOut.data=new(std::nothrow) ac::Bytes(std::move(encoded));bleOut.offset=0;bleOut.started=now;if(!bleOut.data)failSession();}else failSession();delete raw;}}
  if(bleOut.data){if(uint32_t(now-bleOut.started)>120000){bleOut.clear();failSession();}else{size_t count=std::min(chunk,bleOut.data->size()-bleOut.offset);if(tx->notify(bleOut.data->data()+bleOut.offset,count,handle)){bleOut.offset+=count;if(bleOut.offset==bleOut.data->size())bleOut.clear();}}}
 }
 static uint32_t lastStatus=0;if(uint32_t(now-lastStatus)>2000){lastStatus=now;if(usbAppOnline())status(true);if(authorized())status(false);}
 xSemaphoreGive(secureLock);
 if(disconnectRequested.exchange(false)&&bleConnected)server->disconnect(connection.load());
 if(board::rgbLedPin>=0){static bool wasLit=false;uint32_t until=identifyUntil.load();bool lit=until&&int32_t(until-now)>0&&(now/250)%2==0;if(lit!=wasLit){rgbLedWrite(board::rgbLedPin,0,lit?32:0,0);wasLit=lit;}}
 delay(3);
}
