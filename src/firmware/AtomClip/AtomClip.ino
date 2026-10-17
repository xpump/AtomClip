// AtomS3U USB CDC <-> BLE bridge. Clipboard bodies are encrypted by the PCs.
// Arduino ESP32 3.3.12 + NimBLE-Arduino 2.5.1, USBMode=default, CDCOnBoot=cdc.
#include <Arduino.h>
#include <USB.h>
#include <USBHIDKeyboard.h>
#include <NimBLEDevice.h>
#include "Frame.h"
#include <atomic>
#include "Typing.h"

static constexpr char SERVICE[]="a29bc101-12a5-4ea1-81a0-2c4dd9cd0001";
static constexpr char RX_UUID[]="a29bc102-12a5-4ea1-81a0-2c4dd9cd0001";
static constexpr char TX_UUID[]="a29bc103-12a5-4ea1-81a0-2c4dd9cd0001";
static NimBLEServer* server;
static NimBLECharacteristic* tx;
static QueueHandle_t toUsb,toBle;
static SemaphoreHandle_t bleParserLock;
static ac::Parser usbParser,bleParser;
static std::atomic<bool> bleConnected{false},subscribed{false};
static std::atomic<uint16_t> connection{BLE_HS_CONN_HANDLE_NONE};
static std::atomic<uint32_t> usbSeen{0},bleSeen{0};
static std::atomic<bool> haveUsb{false},haveBle{false};

static void enqueue(QueueHandle_t q,ac::Bytes&& b) {
  auto* p=new(std::nothrow) ac::Bytes(std::move(b));
  if(p && xQueueSend(q,&p,0)!=pdTRUE) delete p;
}
static void status(bool usb) {
  uint32_t lastUsb=usbSeen.load(),lastBle=bleSeen.load();
  uint32_t now=millis();
  uint8_t flags[9]={uint8_t(haveUsb && uint32_t(now-lastUsb)<15000),
                    uint8_t(bleConnected && subscribed && haveBle && uint32_t(now-lastBle)<15000),1,1,1,1,1,1,1};
  enqueue(usb?toUsb:toBle,ac::frame(4,flags,9));
}
static bool usbAppOnline() { uint32_t seen=usbSeen.load();return haveUsb && uint32_t(millis()-seen)<15000; }
static void typeResult(const uint8_t* id,uint8_t result) { enqueue(toBle,typing::resultFrame(id,result)); }
static void accept(bool fromUsb,ac::Bytes&& b) {
  uint8_t kind=b[4];
  if(kind==3) { status(fromUsb);return; }
  if(!fromUsb && kind==5) { typing::submit(std::move(b));return; }
  if(kind!=1 && kind!=2) return;
  if(fromUsb) { if(bleConnected && subscribed) enqueue(toBle,std::move(b)); }
  else { uint32_t lastUsb=usbSeen.load();if(haveUsb && uint32_t(millis()-lastUsb)<15000) enqueue(toUsb,std::move(b)); }
}
class ServerCallbacks:public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* s,NimBLEConnInfo& info) override {
    connection=info.getConnHandle();bleConnected=true;subscribed=false;haveBle=false;
    s->updateConnParams(info.getConnHandle(),12,24,0,300);
  }
  void onDisconnect(NimBLEServer*,NimBLEConnInfo&,int) override {
    bleConnected=false;subscribed=false;haveBle=false;connection=BLE_HS_CONN_HANDLE_NONE;
    xSemaphoreTake(bleParserLock,portMAX_DELAY);bleParser.reset();xSemaphoreGive(bleParserLock);
    NimBLEDevice::startAdvertising();
  }
} serverCallbacks;
class RxCallbacks:public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* c,NimBLEConnInfo&) override {
    bleSeen=millis();haveBle=true;
    auto value=c->getValue();
    xSemaphoreTake(bleParserLock,portMAX_DELAY);
    bleParser.feed(reinterpret_cast<const uint8_t*>(value.data()),value.size(),millis(),
      [](ac::Bytes&& b){accept(false,std::move(b));});
    xSemaphoreGive(bleParserLock);
  }
} rxCallbacks;
class TxCallbacks:public NimBLECharacteristicCallbacks {
  void onSubscribe(NimBLECharacteristic*,NimBLEConnInfo&,uint16_t v) override {
    subscribed=(v&1)!=0;
  }
} txCallbacks;

struct Outgoing {
  ac::Bytes* data=nullptr;
  size_t offset=0;
  uint32_t started=0;
  void clear(){delete data;data=nullptr;offset=0;}
  bool next(QueueHandle_t q,uint32_t now){
    // The expiry check must use the same clock sample. A newer millis() here
    // would make the caller's unsigned now-started wrap and drop a fresh frame.
    if(!data && xQueueReceive(q,&data,0)==pdTRUE){offset=0;started=now;}
    return data!=nullptr;
  }
};
static Outgoing usbOut,bleOut;

void setup() {
  toUsb=xQueueCreate(2,sizeof(ac::Bytes*));toBle=xQueueCreate(2,sizeof(ac::Bytes*));
  bleParserLock=xSemaphoreCreateMutex();
  if(!toUsb || !toBle || !bleParserLock) {for(;;)delay(1000);}
  USB.productName("AtomClip Text Bridge");
  // Keep one complete maximum-sized PC write even while BLE notifications drain.
  Serial.setRxBufferSize(20000);Serial.setTxTimeoutMs(10);Serial.enableReboot(false);
  Serial.begin(115200);typing::keyboard.begin();USB.begin();
  NimBLEDevice::init("AtomClip-S3U");NimBLEDevice::setMTU(185);
  server=NimBLEDevice::createServer();server->setCallbacks(&serverCallbacks);
  auto* svc=server->createService(SERVICE);
  auto* rx=svc->createCharacteristic(RX_UUID,NIMBLE_PROPERTY::WRITE,512);rx->setCallbacks(&rxCallbacks);
  tx=svc->createCharacteristic(TX_UUID,NIMBLE_PROPERTY::NOTIFY,512);tx->setCallbacks(&txCallbacks);
  svc->start();
  auto* adv=NimBLEDevice::getAdvertising();
  adv->enableScanResponse(true);adv->addServiceUUID(SERVICE);adv->setName("AtomClip-S3U");adv->start();
  typing::begin(usbAppOnline,typeResult);
}
void loop() {
  uint32_t now=millis();
  uint8_t input[256];
  size_t n=Serial.available();
  if(n){n=Serial.read(input,std::min(n,sizeof(input)));usbSeen=now;haveUsb=true;
    usbParser.feed(input,n,now,[](ac::Bytes&& b){accept(true,std::move(b));});}
  usbParser.expire(millis());
  // BLE callbacks may have received newer fragments since this loop began.
  // Sample the clock after taking the lock so their timestamps are never future.
  xSemaphoreTake(bleParserLock,portMAX_DELAY);bleParser.expire(millis());xSemaphoreGive(bleParserLock);
  if(usbOut.next(toUsb,now)) {
    if(!haveUsb || uint32_t(now-usbSeen.load())>15000 || uint32_t(now-usbOut.started)>25000) usbOut.clear();
    else {
      size_t count=std::min(size_t(256),usbOut.data->size()-usbOut.offset);
      size_t written=Serial.write(usbOut.data->data()+usbOut.offset,count);
      usbOut.offset+=written;if(usbOut.offset==usbOut.data->size())usbOut.clear();
    }
  }
  if(bleOut.next(toBle,now)) {
    uint16_t handle=connection.load();
    if(!bleConnected || !subscribed || uint32_t(now-bleOut.started)>25000) bleOut.clear();
    else {
      size_t mtu=server->getPeerMTU(handle);
      size_t count=std::min(std::min(size_t(180),mtu>3?mtu-3:size_t(20)),bleOut.data->size()-bleOut.offset);
      if(tx->notify(bleOut.data->data()+bleOut.offset,count,handle)) {
        bleOut.offset+=count;if(bleOut.offset==bleOut.data->size())bleOut.clear();
      }
    }
  }
  static uint32_t lastStatus=0;
  if(uint32_t(now-lastStatus)>2000) {
    lastStatus=now;
    if(haveUsb && uint32_t(now-usbSeen.load())<15000)status(true);
    if(bleConnected && subscribed)status(false);
  }
  delay(3);
}
