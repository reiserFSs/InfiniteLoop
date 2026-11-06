use std::collections::HashMap;

/// Client-relative location of the packaged SDK config, reported verbatim in diagnostics.
pub(crate) const PACKAGED_BIN: &str = "PGR_Data/Plugins/KRSDKRes/KRSDK.bin";

pub(crate) fn read_packaged() -> Result<HashMap<String, String>, String> {
    let exe = std::env::current_exe().map_err(|error| format!("cannot locate the game executable: {error}"))?;
    let path = exe
        .parent()
        .ok_or_else(|| "game executable has no parent directory".to_string())?
        .join(PACKAGED_BIN);
    let text = std::fs::read_to_string(&path)
        .map_err(|error| format!("cannot read {} ({error}); the client install is incomplete or not a supported PGR client", path.display()))?;
    Ok(parse(&text))
}

/// One diagnostic line: the region identity KRSDK.bin carries, or why it could not be read.
pub(crate) fn region_line(tag: &str, config: &Result<HashMap<String, String>, String>) -> String {
    let resolved = config.clone().and_then(|config| {
        let package = config.get("KR_PackageName").cloned().unwrap_or_else(|| "<missing>".into());
        identity(&config).map(|id| (package, id))
    });
    match resolved {
        Ok((package, id)) => crate::diag::ok_line(
            tag,
            "KRSDK.bin identity",
            &format!(
                "package={package} game={} app={} channel={} channel_name={} channel_op={} source={PACKAGED_BIN}",
                id.project_id, id.product_id, id.channel_id, id.channel_name, id.channel_op
            ),
        ),
        Err(reason) => crate::diag::failed_line(tag, "KRSDK.bin identity", &reason),
    }
}

/// `KR_*=value` lines of the client's own `PGR_Data/Plugins/KRSDKRes/KRSDK.bin`.
pub(crate) fn parse(text: &str) -> HashMap<String, String> {
    text.lines()
        .filter_map(|line| line.trim_end_matches('\r').split_once('='))
        .map(|(key, value)| (key.to_string(), value.to_string()))
        .collect()
}

/// The identity every region's packaged KRSDK.bin carries (EN G143, KR G286, JP G282, ...).
pub(crate) struct Identity {
    pub channel_id: String,
    pub channel_name: String,
    pub channel_op: String,
    pub project_id: String,
    pub product_id: String,
}

pub(crate) fn identity(config: &HashMap<String, String>) -> Result<Identity, String> {
    let get = |key: &str| {
        config
            .get(key)
            .cloned()
            .ok_or_else(|| format!("packaged SDK config is missing {key}"))
    };
    Ok(Identity {
        channel_id: get("KR_ChannelID")?,
        channel_name: get("KR_ChannelName")?,
        channel_op: get("KR_ChannelOp")?,
        project_id: get("KR_ProjectId")?,
        product_id: get("KR_ProductId")?,
    })
}

#[cfg(test)]
mod tests {
    use super::{identity, parse};

    // Identity lines copied from each region's live KRSDK.bin (4.8.0; the EN Steam line is 4.9.1); the rest of the file is not read.
    const KR: &str = "KR_GameName=海外正式\r\nKR_PackageName=com.herogame.pc.punishing.grayraven.kr\r\nKR_ProductId=A1794\r\nKR_ProjectId=G286\r\nKR_ChannelID=240\r\nKR_ChannelOp=null\r\nKR_ChannelName=PC\r\nKR_SDKAPI_URL=https://sdkapi.kurogame-service.com;https://sdkapi2.kurogame-service.com\r\n";
    const JP: &str = "KR_GameName=海外正式\nKR_PackageName=com.herogame.pc.punishing.grayraven.jp\nKR_ProductId=A1778\nKR_ProjectId=G282\nKR_ChannelID=240\nKR_ChannelOp=null\nKR_ChannelName=PC\n";
    const EN: &str = "KR_ProductId=A1728\nKR_ProjectId=G143\nKR_ChannelID=240\nKR_ChannelOp=null\nKR_ChannelName=PC\n";
    const EN_STEAM_491: &str = "KR_GameName=海外正式\r\nKR_GameVersion=4.9.1\r\nKR_PackageName=com.kurogame.pc.punishing.grayraven.en\r\nKR_ProductId=A1855\r\nKR_ProjectId=G143\r\nKR_ChannelID=205\r\nKR_ChannelOp=null\r\nKR_ChannelName=PC\r\n";

    #[test]
    fn each_region_reports_its_own_packaged_identity() {
        for (bin, project, product, channel) in [(KR, "G286", "A1794", "240"), (JP, "G282", "A1778", "240"), (EN, "G143", "A1728", "240"), (EN_STEAM_491, "G143", "A1855", "205")] {
            let id = identity(&parse(bin)).unwrap();
            assert_eq!((id.project_id.as_str(), id.product_id.as_str()), (project, product));
            assert_eq!(
                (id.channel_id.as_str(), id.channel_name.as_str(), id.channel_op.as_str()),
                (channel, "PC", "null")
            );
        }
    }

    #[test]
    fn values_keep_embedded_equals_and_semicolons() {
        let config = parse(KR);
        assert_eq!(
            config["KR_SDKAPI_URL"],
            "https://sdkapi.kurogame-service.com;https://sdkapi2.kurogame-service.com"
        );
        assert_eq!(parse("KR_Key=a=b==\n")["KR_Key"], "a=b==");
    }

    #[test]
    fn missing_identity_key_names_the_key() {
        let error = identity(&parse("KR_ProjectId=G286\nKR_ChannelID=240\nKR_ChannelOp=null\nKR_ChannelName=PC\n"))
            .err()
            .unwrap();
        assert_eq!(error, "packaged SDK config is missing KR_ProductId");
    }

    #[test]
    fn region_line_names_package_and_ids_or_the_failure() {
        assert_eq!(
            super::region_line("KRSDK", &Ok(parse(KR))),
            "[KRSDK] OK KRSDK.bin identity: package=com.herogame.pc.punishing.grayraven.kr game=G286 app=A1794 channel=240 channel_name=PC channel_op=null source=PGR_Data/Plugins/KRSDKRes/KRSDK.bin"
        );
        assert!(super::region_line("lucia", &Ok(parse(JP))).contains("package=com.herogame.pc.punishing.grayraven.jp game=G282 app=A1778"));
        let missing = super::region_line("lucia", &Ok(parse("KR_ProjectId=G286\n")));
        assert!(missing.starts_with("[lucia] FAILED KRSDK.bin identity: packaged SDK config is missing KR_"), "{missing}");
        let unreadable = super::region_line("lucia", &Err("cannot read x".into()));
        assert!(unreadable.contains("FAILED KRSDK.bin identity: cannot read x | send: ascnet-patch.log"));
    }
}
