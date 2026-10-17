#pragma once
#include <mbedtls/gcm.h>
#include <mbedtls/md.h>
#include <mbedtls/base64.h>
#include <mbedtls/platform_util.h>
#include "PairingKey.h"
#include "PinyinInput.h"
#include "GbkInput.h"

namespace typing {
static USBHIDKeyboard keyboard;
static QueueHandle_t jobs;
static bool (*online)();
static void (*result)(const uint8_t*,uint8_t);
static uint8_t recent[16][16],outcomes[16];
static unsigned recentCount=0,recentNext=0;
static constexpr char BOOTSTRAP[] = "powershell -NoP -NonI -C \"$s='';while($x=[Console]::ReadLine()){$s+=$x};$m=[IO.MemoryStream]::new([Convert]::FromBase64String($s));Set-Clipboard ([IO.StreamReader]::new([IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]0))).ReadToEnd()\"";
static_assert(sizeof(BOOTSTRAP)<259,"Run dialog command must remain short");
static constexpr char RECEIVER_START[] = "powershell -NoP -STA -NoExit -C \"Remove-Module PSReadLine -EA SilentlyContinue\"";
inline bool validJob(const ac::Bytes& p) {
  if(p.size()>5 && p[0]==6)return p[1]>=1 && p[1]<=50 && p[2]>=1 && p[2]<=50 && p[3]>=1 && p[3]<=100 && p[4]>=1 && p[4]<=100 && gbk::valid(p.data()+5,p.size()-5);
  if(p.size()==3)return p[0]==2 && p[1]==0 && p[2]>=1 && p[2]<=50;
  if(p.size()>3 && p[0]==5)return p[1]>=20 && p[1]<=200 && p[2]>=1 && p[2]<=50 && pinyin::valid(p.data()+3,p.size()-3);
  if(p.size()>3 && p[0]==3){
    if(p.size()>16387 || p[1]!=0 || p[2]<1 || p[2]>50)return false;
    for(size_t i=3;i<p.size();i++)if((p[i]<32 || p[i]>126) && p[i]!='\n' && p[i]!='\t')return false;
    return true;
  }
  if(p.size()>=24 && p[0]==4)return p[1]<=1 && p[2]>=1 && p[2]<=50 && p[3]>=3 && p[3]<=60 && p[4]==0x1f && p[5]==0x8b && ac::le32(p.data()+p.size()-4)<=16384;
  return p.size()>=23 && p[0]==1 && p[1]<=1 && p[2]>=1 && p[2]<=50 && p[3]==0x1f && p[4]==0x8b &&
    ac::le32(p.data()+p.size()-4)<=16384;
}
inline ac::Bytes resultFrame(const uint8_t* id,uint8_t status) {
  uint8_t payload[33],signedData[18],mac[32];
  memcpy(payload,id,16);payload[16]=status;signedData[0]=6;memcpy(signedData+1,payload,17);
  mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),PAIRING_KEY,32,signedData,18,mac);
  memcpy(payload+17,mac,16);return ac::frame(6,payload,33);
}
inline bool allowed() { return !online() && digitalRead(41)!=LOW; }
inline bool waitSafe(uint32_t duration) {
  uint32_t start=millis();
  for(;;){uint32_t elapsed=uint32_t(millis()-start);if(elapsed>=duration)break;if(!allowed())return false;delay(std::min(uint32_t(5),duration-elapsed));}
  return allowed();
}
inline bool ascii(const char* s,size_t length,uint8_t gap) {
  for(size_t i=0;i<length;i++){
    if(!allowed())return false;
    if(s[i]=='\n')keyboard.write(KEY_RETURN);
    else if(!keyboard.write(uint8_t(s[i])))return false;
    if(gap && !waitSafe(gap))return false;
  }return true;
}
inline void chord(uint8_t modifier,uint8_t key) { keyboard.press(modifier);keyboard.press(key);delay(35);keyboard.releaseAll(); }
inline bool run(const ac::Bytes& plain) {
  if(plain[0]==6) {
    if(!waitSafe(3000))return false;
    return gbk::plan(plain.data()+5,plain.size()-5,
      [&plain](uint8_t c){char one=char(c);return ascii(&one,1,plain[2]);},
      [&plain](uint16_t code){
        bool ok=gbk::emit(code,plain[1],plain[2],plain[3],plain[4],
          [](uint8_t key,bool down){return (down?keyboard.pressRaw(key):keyboard.releaseRaw(key))!=0;},
          [](uint8_t ms){delay(ms);});
        keyboard.releaseAll();return ok;
      },[](){return allowed();});
  }
  if(plain[0]==5) {
    if(!waitSafe(3000))return false;
    uint8_t gap=plain[2],settle=plain[1];
    return pinyin::plan(plain.data()+3,plain.size()-3,
      [gap](uint8_t c){char one=char(c);return ascii(&one,1,gap);},
      [gap,settle](uint16_t code){
        char keys[8];pinyin::vuc(code,keys);
        if(!ascii(keys,7,gap) || !waitSafe(settle))return false;
        keyboard.write(' ');return waitSafe(settle);
      },
      [settle](bool){
        if(!allowed())return false;
        keyboard.press(KEY_LEFT_SHIFT);bool ok=waitSafe(15);keyboard.releaseAll();
        return ok && waitSafe(settle);
      });
  }
  if(plain[0]==3)return waitSafe(3000) && ascii(reinterpret_cast<const char*>(plain.data()+3),plain.size()-3,plain[2]);
  if(plain[0]==2){
    if(!waitSafe(3000))return false;
    chord(KEY_LEFT_GUI,'r');if(!waitSafe(700))return false;
    chord(KEY_LEFT_CTRL,'a');
    if(!ascii(RECEIVER_START,strlen(RECEIVER_START),plain[2]))return false;
    keyboard.write(KEY_RETURN);
    // Only open the fixed system host. The user pastes and executes separately.
    return waitSafe(3000);
  }
  bool paste=plain[1]!=0;uint8_t gap=plain[2];
  size_t header=plain[0]==4?4:3;
  uint32_t startup=plain[0]==4?uint32_t(plain[3])*1000:10000;
  size_t gzipSize=plain.size()-header,encodedSize=0;
  ac::Bytes encoded(4*((gzipSize+2)/3)+1);
  if(mbedtls_base64_encode(encoded.data(),encoded.size(),&encodedSize,plain.data()+header,gzipSize)!=0)return false;
  if(!waitSafe(3000))return false;
  chord(KEY_LEFT_GUI,'r');
  if(!waitSafe(700))return false;
  chord(KEY_LEFT_CTRL,'a');
  // Keep the short launch command conservative; accelerate only the Base64 data.
  if(!ascii(BOOTSTRAP,strlen(BOOTSTRAP),std::max(uint8_t(4),gap)))return false;
  keyboard.write(KEY_RETURN);
  // Startup time must be validated on the actual Win10. No console command is
  // taken from user text; only generated Base64 is sent into Console.ReadLine.
  if(!waitSafe(startup))return false;
  for(size_t p=0;p<encodedSize;p+=120){
    size_t n=std::min(size_t(120),encodedSize-p);
    if(!ascii(reinterpret_cast<const char*>(encoded.data()+p),n,gap))return false;
    keyboard.write(KEY_RETURN);if(!waitSafe(10))return false;
  }
  if(!ascii("\n",1,gap) || !waitSafe(1800))return false;
  if(paste)chord(KEY_LEFT_CTRL,'v');
  mbedtls_platform_zeroize(encoded.data(),encoded.size());
  return true;
}
inline void worker(void*) {
  for(;;){
    ac::Bytes* frame=nullptr;if(xQueueReceive(jobs,&frame,portMAX_DELAY)!=pdTRUE)continue;
    if(!frame)continue;
    size_t n=frame->size()>=57?frame->size()-57:0;
    ac::Bytes plain(n);
    uint8_t id[16]={0};bool authentic=false;
    if(frame->size()>=57 && n>=3){
      const uint8_t* b=frame->data()+9;memcpy(id,b,16);
      mbedtls_gcm_context g;mbedtls_gcm_init(&g);
      if(mbedtls_gcm_setkey(&g,MBEDTLS_CIPHER_ID_AES,PAIRING_KEY,256)==0)
        authentic=mbedtls_gcm_auth_decrypt(&g,n,b+16,12,b,16,b+28+n,16,b+28,plain.data())==0;
      mbedtls_gcm_free(&g);
    }
    delete frame;
    if(!authentic)continue;
    int known=-1;for(unsigned i=0;i<recentCount;i++)if(memcmp(recent[i],id,16)==0){known=int(i);break;}
    if(known>=0){result(id,outcomes[known]);mbedtls_platform_zeroize(plain.data(),plain.size());continue;}
    unsigned slot=recentNext;recentNext=(recentNext+1)%16;recentCount=std::min(16u,recentCount+1);
    memcpy(recent[slot],id,16);outcomes[slot]=3;
    bool valid=validJob(plain);
    if(!valid || !allowed()){result(id,3);mbedtls_platform_zeroize(plain.data(),plain.size());continue;}
    result(id,0);
    outcomes[slot]=run(plain)?2:4;keyboard.releaseAll();
    mbedtls_platform_zeroize(plain.data(),plain.size());result(id,outcomes[slot]);
  }
}
inline void begin(bool (*isOnline)(),void (*sendResult)(const uint8_t*,uint8_t)) {
  online=isOnline;result=sendResult;pinMode(41,INPUT_PULLUP);
  jobs=xQueueCreate(1,sizeof(ac::Bytes*));
  if(jobs)xTaskCreate(worker,"typing",8192,nullptr,1,nullptr);
}
inline void submit(ac::Bytes&& frame) {
  if(!jobs)return;
  auto* b=new(std::nothrow) ac::Bytes(std::move(frame));
  if(b && xQueueSend(jobs,&b,0)!=pdTRUE)delete b;
}
}
