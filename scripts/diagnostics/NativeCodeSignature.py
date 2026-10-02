"""What a game build must share with the verified build before the native scripts touch it.

The native scripts hook, call and compare return addresses at fixed offsets taken from one build of the
game. Instead of accepting only that exact executable, any build is accepted when its sections start
where the verified build's do and the bytes at every one of those offsets are identical: a patch that
leaves this code alone keeps working, and one that moves or changes it is refused before anything is
attached, so nothing in the game is changed.

Regenerate the table from a verified build with `python NativeCodeSignature.py <tlou-ii.exe>` after
adding a site to the native scripts.
"""
from pathlib import Path
import hashlib
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from InspectPeVa import PeError, read_layout, va_to_offset

VERIFIED_SHA256 = 'CAC7F729EAE6FAB743616B8216ACF20E9F05D8E51DA0E3B7A4892147569DD357'
UNVERIFIED_BUILD = 'Game code differs from the verified build'
# Every code site of the native modes is pinned with the 32 bytes the scripts fingerprint.
CODE_BYTES = 32
# Return addresses the recovery bridge compares callers against, pinned around the call they follow,
# and the profile stat service's vtable through slot 9, whose setter the bridge calls.
EXTRA_SITES = ((0xFD7909 - 8, 16), (0x1B802C3 - 8, 16), (0x1B8152F - 8, 16), (0x1B82059 - 8, 16),
               (0x1BE144E - 8, 16), (0x2AF57D8, 0x50))

# Generated from the verified build (see the module docstring).
SECTIONS = {'.text': 0x1000, '.rdata': 0x2a86000, '.data': 0x3539000, '.pdata': 0x9652000, '_RDATA': 0x9841000, '.rsrc': 0x9855000, '.reloc': 0x99c6000}
PINNED = {
    0xd49530: '405356574881ec80000000488b05d67bb3024833c44889442470488bf1c5f9ef',
    0xd49630: '405356574881ec80000000488b05d67ab3024833c44889442470488bf1c5f9ef',
    0xd95530: '488bc448894808555356574154415541564157488d68a14881eca8000000c5f8',
    0xd966f0: '48895c2410574883ec30488bd9488d052401f60148890133d2e8c2f8d3ff33d2',
    0xdacc20: '488bc453565741544155415641574881ecf0040000c5f82970b8c5f82978a8c5',
    0xdaf950: '48895c24205556574154415541564157488d6c24d94881ecb0000000488b05a5',
    0xdafea0: '48895c240848896c24104889742418574883ec40498bf1498bf8488bea488bd9',
    0xdc1780: '4053565741544155415641574881ec70020000c5f829b42460020000488b0575',
    0xfd7870: '40534881ecb0000000488b0598988a024833c448898424a00000004c8b055634',
    0xfd7901: '331803e8b7790600c5f9efc0c5fc1144',
    0xfdcd90: '4c8bdc49895b10574881ec80030000488b5908c683120e0b000133ff89791840',
    0xfddbf0: '48895c24185556574154415541564157488dac24c0f5ffff4881ec400b0000c5',
    0xfe7b10: '4883ec28e84716b5004c8bc833c04d85c97423448b05ee501503498b4940418b',
    0xfe8fc0: '48895c2410488974241848897c2420554154415541564157488d6c24c94881ec',
    0xfe96f0: '40534883ec20488bd948899150140b0044888149140b00e8f403b500488b15c5',
    0xff6920: '48896c2410488974241848897c242041564883ec60488b05dca788024833c448',
    0x103f2c0: '48895c24104889742418574883ec308bfa488bd980b9c8720000000f859a0200',
    0x1042680: '48895c2410488974241848897c24205541564157488d6c24a04881ec60010000',
    0x1042aa0: '40534883ec60488b056be683024833c448894424584533c0488d0db11c2f0833',
    0x1042b80: '40574883ec40488b058be583024833c448894424304533c0488d0dd11b2f0833',
    0x10434c0: '4883ec68488b054ddc83024833c448894424584533c0488d0d93122f0833d2e8',
    0x1046290: '48895c2410488974241848897c2420554154415541564157488dac2420feffff',
    0x1046f80: '4883ec284533c0488d0de2d72e0833d2e8cb7ab2004885c07413488b80200100',
    0x1046fc0: '4883ec284533c0488d0da2d72e0833d2e88b7ab2004885c07413488b80200100',
    0x1048520: '48895c2408574883ec20c681c872000000488bd9c78120050000020000004881',
    0x1048960: '488951504c894158c3ccccccccccccccc4c17c1000c5fc1181c80400008991e8',
    0x1049bf0: '48895c2410488974241848897c2420554154415541564157488d6c24904881ec',
    0x104c700: '33c0380508e610030f95c0488901488bc1c3cccccccccccccccccccccccccccc',
    0x105e930: '48895c2408574883ec20803d17361003000fb6da488bf97421488d0d40c30f03',
    0x105f830: '48897c24185541564157488d6c24b94881ecb0000000488b05cb1882024833c4',
    0x11f9300: '48895c240848896c24104889742418574883ec30418bd88bfa488bf1488d2d3d',
    0x1200ac0: '48895c241055565741544155415641574881ec90000000c5f829b42480000000',
    0x120af40: '40555356574154415541564157488d6c24e84881ec18010000488b05b8616702',
    0x120d090: '40535557415441574883ec408b9c24980000004d8be18bbc24900000004d8bf8',
    0x120dac0: '488b02488b400848051b0400004883e0f8c3cccccccccccccccccccccccccccc',
    0x1247360: '40534883ec20488bc2488bd94885d27423803a00741e48ba25232284e49cf2cb',
    0x124c1d0: '0fb60184c0741f49b8b3010000000100000fb6c0488d49014833d00fb601490f',
    0x133d910: '488b81800000004885c0740e488b40304885c07405488b4008c333c0c3cccccc',
    0x133d950: '488b81800000004885c0740e488b40384885c07405488b4008c333c0c3cccccc',
    0x133ebb0: '40534883ec20488bd9e872d02200c5f057c9c5f82fc1770a807b09000f8c9e00',
    0x133ec5e: '806308df900fb64309c0e8052401884332904883c4205bc3cccccccccccccccc',
    0x1b37160: '4053574883ec78488b05aa9fd4014833c44889442468488bf9c5f9efc033c0c5',
    0x1b37310: '405356574881ec80000000488b05f69dd4014833c44889442470488bf9c5f9ef',
    0x1b39bf0: '4883ec28488b0dbdcdc9014c8bc233d2418bc0f7f14863c2488b1519cdc90148',
    0x1b6e000: '48895c2410564883ec30448b8160020000488bda33d2488bf14585c07457448b',
    0x1b6ea60: '48895c240848896c24104889742418574883ec20803df55c7c0700410fb6f00f',
    0x1b6fa70: '488bc44889581048896818488970205741544155415641574883ec70410fb6d9',
    0x1b7d080: '4883b9a805000000742133c04883c1588b1185d2740583fa077c1348ffc04881',
    0x1b7fc60: '40555356574154415541564157488d6c24d84881ec280100004963f9897c2434',
    0x1b80080: '40534881ec9000000033c0c7442438ffffffff83b9e005000014488bd9488944',
    0x1b802bb: 'c5f877e89df9ffff84c074074488bbc8',
    0x1b81527: 'c5f877e831e7ffff4881c498000000c3',
    0x1b82051: '8b4f08e827d82a0084c00f85e6000000',
    0x1b83f70: '48895c240848896c242056574154415641574883ec70488b058bd1cf014833c4',
    0x1b843a0: '48895c240848896c241056574154415641574881ec80010000488b0558cdcf01',
    0x1bdb630: '48895c24185556574883ec70488b05d55aca014833c44889442460410fb6d941',
    0x1bdeed0: '48895c24105556574154415541564157488dac2430ffffff4881ecd001000048',
    0x1bdf840: '4883ec38488b02488bd14889442448488d0d1a4f7507488b4424604889442428',
    0x1be1446: '0000ff9080000000488b742448488b6c',
    0x1c55a3c: '498b16488bc8488bf0e8a603000085c0780f4863c8488b4630488d14498b7cd0',
    0x1c55df0: '48895c2410488974241855574156488d6c24b94881ecb0000000448b91800200',
    0x1c57bb0: '48895c241048896c2418565741574883ec20448bfa410fb6e9498bd0498bd848',
    0x1e2f880: '4883ec3883790c02488bc2740732c04883c438c3448b414841b902000000488b',
    0x1e35d60: '33c04885c97425448b0d0adc6507488b5140458bc149c1e9064183e03fc422bb',
    0x1e39660: '33c04885c97425448b0d3aa56507488b5140458bc149c1e9064183e03fc422bb',
    0x1e39880: '4883ec28e8f75a0100488bc8e8cfc4ffff4885c0741ef680a108000001741748',
    0x1e3b920: '48895c242055565741544155415641574883ec60488b05dd57a4014833c44889',
    0x1e4df10: '48895c24184889742420574881ec50040000488b05ef31a3014833c448898424',
    0x1e4f190: '4883ec28e877e74eff4885c07427448b05e38c6407488b4840418bd048c1ea06',
    0x1e4f380: '40534883ec20e805feffff488bd84885c07420488bc8e8d50000004885c07410',
    0x1e4f470: '4883ec28e8d7e44eff4885c07427448b05038a6407488b4840418bd048c1ea06',
    0x2af57d8: '70a9334001000000e0aa33400100000060ab33400100000080ab334001000000b07bc541010000005079c54101000000c066c54101000000b065c541010000005051c541010000004072c54101000000',
}


def signature_mismatch(image):
    """None when the image matches the verified build at every pinned site, otherwise where it differs."""
    try:
        preferred, headers, sections = read_layout(image)
        starts = {section.name: section.virtual_address for section in sections}
        if starts != SECTIONS:
            return 'section layout'
        for rva, expected in PINNED.items():
            offset, _ = va_to_offset(preferred + rva, preferred, headers, sections)
            if image[offset:offset + len(expected) // 2].hex() != expected:
                return f'code at {rva:#x}'
    except PeError as error:
        return f'unreadable executable: {error}'
    return None


def main(argv):
    from NativeCheckpointTrigger import (CODE_RVAS, COLLECTION_RVAS, ENCOUNTER_RVAS, LIFECYCLE_RVAS, LOAD_RVAS,
                                         RECOVERY_RVAS, RESULTS_RVAS, RESUME_RVAS)
    image = Path(argv[1]).read_bytes()
    if hashlib.sha256(image).hexdigest().upper() != VERIFIED_SHA256:
        raise SystemExit('Generate the table from the verified build only')
    preferred, headers, sections = read_layout(image)
    sites = {rva: CODE_BYTES for group in (CODE_RVAS, LIFECYCLE_RVAS, RESUME_RVAS, LOAD_RVAS, COLLECTION_RVAS,
                                           RECOVERY_RVAS, RESULTS_RVAS, ENCOUNTER_RVAS) for rva in group}
    sites.update(EXTRA_SITES)
    print('SECTIONS = {' + ', '.join(f'{section.name!r}: {section.virtual_address:#x}' for section in sections) + '}')
    print('PINNED = {')
    for rva, size in sorted(sites.items()):
        offset, _ = va_to_offset(preferred + rva, preferred, headers, sections)
        print(f"    {rva:#x}: '{image[offset:offset + size].hex()}',")
    print('}')


if __name__ == '__main__':
    main(sys.argv)
