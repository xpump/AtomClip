#pragma once
#include <Preferences.h>
#include <esp_mac.h>
#include <esp_random.h>
#include "SecureCore.h"
#include "BoardProfile.h" // Generated from boards/profiles.json by the build script.
#include "PairingKey.h"   // Legacy data pairing only, never a device-control credential.

namespace device {
static Preferences preferences;
static uint8_t id[16]{},controlKey[32]{},dataKey[32]{};
static bool configured=false;
static constexpr size_t CONFIG_SIZE=76;
inline bool allZero(const uint8_t* b,size_t n){uint8_t value=0;while(n--)value|=*b++;return value==0;}
inline bool save(const uint8_t* control,const uint8_t* data,bool bound){
 uint8_t blob[CONFIG_SIZE]{};memcpy(blob,"ACF2",4);blob[4]=bound?1:0;memcpy(blob+8,control,32);memcpy(blob+40,data,32);ac::put32(blob+72,ac::crc(blob,72));
 uint8_t readback[CONFIG_SIZE]{};bool ok=preferences.putBytes("config",blob,sizeof(blob))==sizeof(blob)&&preferences.getBytes("config",readback,sizeof(readback))==sizeof(readback)&&secure::equal(blob,readback,sizeof(blob));
 if(ok){memcpy(controlKey,control,32);memcpy(dataKey,data,32);configured=bound;}
 mbedtls_platform_zeroize(blob,sizeof(blob));mbedtls_platform_zeroize(readback,sizeof(readback));return ok;
}
inline bool begin(){
 uint8_t factory[6],digest[32];if(esp_efuse_mac_get_default(factory)!=ESP_OK||allZero(factory,6))return false;
 ac::Bytes identity;secure::domain(identity,"AtomClip/DeviceId/v2");secure::append(identity,factory,6);if(!secure::sha(identity.data(),identity.size(),digest))return false;memcpy(id,digest,16);mbedtls_platform_zeroize(digest,32);
 if(!preferences.begin("atomclip2",false))return false;
 size_t n=preferences.getBytesLength("config");
 if(n){
  uint8_t b[CONFIG_SIZE]{};if(n!=sizeof(b)||preferences.getBytes("config",b,sizeof(b))!=sizeof(b)||memcmp(b,"ACF2",4)!=0||b[4]>1||b[5]!=0||b[6]!=0||b[7]!=0||ac::crc(b,72)!=ac::le32(b+72)||allZero(b+40,32)||(b[4]&&allZero(b+8,32)))return false;
  memcpy(controlKey,b+8,32);memcpy(dataKey,b+40,32);configured=b[4]!=0;mbedtls_platform_zeroize(b,sizeof(b));return true;
 }
 uint8_t fresh[32];
#if AC_LEGACY_DATA_PAIRING
 memcpy(fresh,PAIRING_KEY,32);
#else
 esp_fill_random(fresh,32);
#endif
 bool ok=!allZero(fresh,32)&&save(controlKey,fresh,false);mbedtls_platform_zeroize(fresh,32);return ok;
}
inline ac::Bytes info(){
 ac::Bytes b(40);memcpy(b.data(),"ACI2",4);b[4]=2;b[5]=1;b[6]=board::id;b[7]=board::id>>8;ac::put32(b.data()+8,board::capabilities);ac::put32(b.data()+12,board::flashBytes);b[16]=0;b[17]=2;b[18]=0;memcpy(b.data()+20,id,16);b[36]=configured?3:2;return b;
}
inline bool provision(const uint8_t* control,const uint8_t* data,bool replace){return (!configured||replace)&&!allZero(control,32)&&!allZero(data,32)&&!secure::equal(control,data,32)&&save(control,data,true);}
inline bool reset(){uint8_t empty[32]{},fresh[32];esp_fill_random(fresh,32);bool ok=!allZero(fresh,32)&&save(empty,fresh,false);mbedtls_platform_zeroize(fresh,32);return ok;}
}
