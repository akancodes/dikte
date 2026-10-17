import XCTest
@testable import DikteCore

final class UpdatesTests: XCTestCase {
    let hash = "4df85caed0a5f5fc66fd3b6e3ca7a7a5d692c3802fcf30c70e306f2253cc52a9"
    let installed = AppVersion("0.4.1")!

    /// The shape of GitHub's /releases/latest answer, cut down to what is read.
    func json(tag: String = "v0.5.0", digest: String? = nil, prerelease: Bool = false,
              url: String = "https://github.com/akancodes/dikte/releases/download/v0.5.0/Dikte-mac.zip") -> Data {
        let digestField = digest == "" ? "" : "\"digest\": \"\(digest ?? "sha256:" + hash)\","
        return Data("""
        {
          "tag_name": "\(tag)", "draft": false, "prerelease": \(prerelease),
          "assets": [
            { "name": "Dikte.exe", "size": 52333225, "digest": "sha256:\(hash)",
              "browser_download_url": "https://github.com/akancodes/dikte/releases/download/v0.5.0/Dikte.exe" },
            { "name": "Dikte-mac.zip", "size": 562582, \(digestField)
              "browser_download_url": "\(url)" }
          ]
        }
        """.utf8)
    }

    func newer(_ data: Data, asset: String = Updates.macAsset) throws -> Release? {
        try Updates.newer(data, than: installed, asset: asset)
    }

    func testANewerReleaseIsFoundWithItsZip() throws {
        let r = try XCTUnwrap(newer(json()))
        XCTAssertEqual(r.version, AppVersion("0.5.0"))
        XCTAssertEqual(r.version.description, "0.5.0")
        XCTAssertEqual(r.size, 562582)
        XCTAssertEqual(r.sha256, hash)
        XCTAssertEqual(r.url.lastPathComponent, "Dikte-mac.zip")
    }

    func testNotNewerOrOddTagsAreIgnored() throws {
        // The same version (0.4.1 equals 0.4.1.0), an older one, a pre-release suffix, no "v".
        for tag in ["v0.4.1", "v0.4.1.0", "v0.4.0", "v0.5.0-beta", "0.5.0", "v", "vdev"] {
            XCTAssertNil(try newer(json(tag: tag)), tag)
        }
    }

    func testPrereleasesAreIgnored() throws {
        XCTAssertNil(try newer(json(prerelease: true)))
    }

    func testAZipWithoutAProperDigestIsNeverOffered() throws {
        for digest in ["", "sha1:abc", "sha256:nothex", "sha256:" + String(hash.dropLast())] {
            XCTAssertNil(try newer(json(digest: digest)), digest)
        }
    }

    func testAnUpperCaseDigestIsReadAsLowerCase() throws {
        XCTAssertEqual(try newer(json(digest: "sha256:" + hash.uppercased()))?.sha256, hash)
    }

    func testOnlyGithubDownloadsAreTaken() throws {
        XCTAssertNil(try newer(json(url: "https://example.com/Dikte-mac.zip")))
        XCTAssertNil(try newer(json(url: "http://github.com/akancodes/dikte/releases/download/v0.5.0/Dikte-mac.zip")))
        XCTAssertNil(try newer(json(url: "https://github.com.evil.example/Dikte-mac.zip")))
    }

    func testAMissingAssetMeansNoUpdate() throws {
        XCTAssertNil(try newer(json(), asset: "Dikte-arm64.zip"))
    }

    func testNotJsonThrows() {
        XCTAssertThrowsError(try newer(Data("<html>rate limited</html>".utf8)))
    }

    func testVersionsCompareByNumberNotText() {
        XCTAssertLessThan(AppVersion("0.9.0")!, AppVersion("0.10.0")!)
        XCTAssertEqual(AppVersion("1.2")!, AppVersion("1.2.0.0")!)
        XCTAssertNil(AppVersion("dev"))
        XCTAssertNil(AppVersion("1"))
        XCTAssertNil(AppVersion("1..2"))
        XCTAssertNil(AppVersion("1.2.3.4.5"))
    }

    func testAutoUpdateIsOnUnlessTurnedOff() throws {
        XCTAssertTrue(Settings().autoUpdate)
        XCTAssertTrue(try Settings.decode(Data("{}".utf8)).autoUpdate)
        XCTAssertFalse(try Settings().merging(["autoUpdate": false]).autoUpdate)
    }
}
