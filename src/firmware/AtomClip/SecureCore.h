#pragma once
#include "Frame.h"
#include <mbedtls/md.h>
#include <mbedtls/gcm.h>
#include <mbedtls/platform_util.h>
#include <cstring>

// Wire contract: docs/DEVICE_V2.md. Shared deterministic vectors: tests/vectors/v2.properties.
namespace secure {
constexpr size_t INFO_SIZE=40,MAX_INNER=16525;
inline ac::Bytes bytes(const uint8_t* b,size_t n){return ac::Bytes(b,b+n);}
inline void append(ac::Bytes& out,const uint8_t* b,size_t n){out.insert(out.end(),b,b+n);}
inline bool equal(const uint8_t* a,const uint8_t* b,size_t n){uint8_t difference=0;while(n--)difference|=*a++^*b++;return difference==0;}
inline bool sha(const uint8_t* b,size_t n,uint8_t out[32]){return mbedtls_md(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),b,n,out)==0;}
inline bool mac(const uint8_t* key,const uint8_t* b,size_t n,uint8_t out[32]){return mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),key,32,b,n,out)==0;}
inline void domain(ac::Bytes& b,const char* name){append(b,reinterpret_cast<const uint8_t*>(name),strlen(name)+1);}
inline bool proof(const uint8_t* key,const char* role,const uint8_t* info,const uint8_t* client,const uint8_t* server,uint8_t out[32]){
 ac::Bytes message;domain(message,role);append(message,info,40);append(message,client,32);append(message,server,32);return mac(key,message.data(),message.size(),out);
}
inline ac::Bytes control(uint8_t type,const uint8_t* data,size_t n){
 if(type<1||type>4||n>256)return {};
 ac::Bytes b(11+n);memcpy(b.data(),"ACV2",4);b[4]=type;b[5]=n;b[6]=n>>8;if(n)memcpy(b.data()+7,data,n);ac::put32(b.data()+7+n,ac::crc(b.data()+4,3+n));return b;
}
inline bool validControl(const ac::Bytes& b,uint8_t type,size_t length){return b.size()==11+length&&length<=256&&memcmp(b.data(),"ACV2",4)==0&&b[4]==type&&(size_t(b[5])|(size_t(b[6])<<8))==length&&ac::crc(b.data()+4,3+length)==ac::le32(b.data()+7+length);}
class Stream {
 bool auth;ac::Bytes pending;size_t expected=0;uint32_t first=0,last=0;
public:
 explicit Stream(bool authentication):auth(authentication){}
 void reset(){pending.clear();expected=0;}
 bool expired(uint32_t now)const{return !pending.empty()&&(uint32_t(now-last)>(auth?3000u:5000u)||uint32_t(now-first)>(auth?15000u:120000u));}
 bool feed(const uint8_t* b,size_t n,uint32_t now,const std::function<bool(ac::Bytes&&)>& emit){
  if(expired(now)){reset();return false;}
  while(n--){if(pending.empty())first=now;pending.push_back(*b++);size_t size=pending.size();
   if(size==4&&memcmp(pending.data(),auth?"ACV2":"ACS2",4)!=0){reset();return false;}
   if(size==(auth?7u:16u)){size_t length=auth?size_t(pending[5])|(size_t(pending[6])<<8):ac::le32(pending.data()+12);if(length<(auth?0u:13u)||length>(auth?256u:MAX_INNER)){reset();return false;}expected=length+(auth?11u:32u);}
   if(expected&&size==expected){ac::Bytes frame;frame.swap(pending);expected=0;if(!emit(std::move(frame))){reset();return false;}}
  }
  last=now;return true;
 }
};
class Session {
 uint8_t sendKey[32]{},receiveKey[32]{},sendPrefix[4]{},receivePrefix[4]{};uint64_t sendSequence=0,receiveSequence=0;
 static void put64(uint8_t* b,uint64_t n){for(int i=0;i<8;i++)b[i]=n>>(8*i);}
 static bool expand(const uint8_t* prk,const char* label,uint8_t* out,size_t n){ac::Bytes b;domain(b,label);b.push_back(1);uint8_t digest[32];bool ok=mac(prk,b.data(),b.size(),digest);if(ok)memcpy(out,digest,n);mbedtls_platform_zeroize(digest,32);return ok;}
public:
 bool active=false;
 ~Session(){reset();}
 void reset(){active=false;sendSequence=receiveSequence=0;mbedtls_platform_zeroize(sendKey,32);mbedtls_platform_zeroize(receiveKey,32);mbedtls_platform_zeroize(sendPrefix,4);mbedtls_platform_zeroize(receivePrefix,4);}
 bool derive(const uint8_t* key,const uint8_t* info,const uint8_t* client,const uint8_t* server,bool isClient=false){
  reset();ac::Bytes b;domain(b,"AtomClip/v2/session");append(b,info,40);append(b,client,32);append(b,server,32);uint8_t salt[32]{},prk[32]{};
  bool ok=sha(b.data(),b.size(),salt)&&mac(salt,key,32,prk);
  ok=ok&&expand(prk,isClient?"AtomClip/v2/c2d/key":"AtomClip/v2/d2c/key",sendKey,32)&&expand(prk,isClient?"AtomClip/v2/d2c/key":"AtomClip/v2/c2d/key",receiveKey,32)&&expand(prk,isClient?"AtomClip/v2/c2d/nonce":"AtomClip/v2/d2c/nonce",sendPrefix,4)&&expand(prk,isClient?"AtomClip/v2/d2c/nonce":"AtomClip/v2/c2d/nonce",receivePrefix,4);
  mbedtls_platform_zeroize(salt,32);mbedtls_platform_zeroize(prk,32);active=ok;if(!ok)reset();return ok;
 }
 bool seal(const ac::Bytes& plain,ac::Bytes& out){
  if(!active||sendSequence>=0x7fffffffffffffffULL||plain.size()<13||plain.size()>MAX_INNER)return false;
  out.resize(32+plain.size());memcpy(out.data(),"ACS2",4);put64(out.data()+4,sendSequence);ac::put32(out.data()+12,plain.size());uint8_t nonce[12];memcpy(nonce,sendPrefix,4);put64(nonce+4,sendSequence);
  mbedtls_gcm_context g;mbedtls_gcm_init(&g);bool ok=mbedtls_gcm_setkey(&g,MBEDTLS_CIPHER_ID_AES,sendKey,256)==0&&mbedtls_gcm_crypt_and_tag(&g,MBEDTLS_GCM_ENCRYPT,plain.size(),nonce,12,out.data(),16,plain.data(),out.data()+16,16,out.data()+16+plain.size())==0;mbedtls_gcm_free(&g);
  if(ok)sendSequence++;else{reset();out.clear();}return ok;
 }
 bool open(const ac::Bytes& frame,ac::Bytes& plain){
  uint8_t sequence[8];put64(sequence,receiveSequence);
  if(!active||receiveSequence>=0x7fffffffffffffffULL||frame.size()<45||frame.size()>MAX_INNER+32||memcmp(frame.data(),"ACS2",4)!=0||ac::le32(frame.data()+12)!=frame.size()-32||!equal(frame.data()+4,sequence,8)){reset();return false;}
  size_t length=frame.size()-32;plain.resize(length);uint8_t nonce[12];memcpy(nonce,receivePrefix,4);memcpy(nonce+4,sequence,8);mbedtls_gcm_context g;mbedtls_gcm_init(&g);
  bool ok=mbedtls_gcm_setkey(&g,MBEDTLS_CIPHER_ID_AES,receiveKey,256)==0&&mbedtls_gcm_auth_decrypt(&g,length,nonce,12,frame.data(),16,frame.data()+16+length,16,frame.data()+16,plain.data())==0;mbedtls_gcm_free(&g);
  if(ok)receiveSequence++;else{reset();mbedtls_platform_zeroize(plain.data(),plain.size());plain.clear();}return ok;
 }
};
}
